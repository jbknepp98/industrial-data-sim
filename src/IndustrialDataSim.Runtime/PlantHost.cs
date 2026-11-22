using System.Text.Json;
using IndustrialDataSim.Core.Plant;
using IndustrialDataSim.Core.Simulation;

namespace IndustrialDataSim.Runtime;

/// <summary>Counters for one host cycle, reported to the caller and the log.</summary>
public sealed record PlantCycle(DateTimeOffset ThroughUtc, int RowsApplied, int PointsPublished, int Batches, bool CaughtUp);

/// <summary>
/// Runs an admitted plant live: backfill first, then real time, until stopped.
///
/// Each cycle runs the deterministic engine from the latest snapshot (or the
/// plant start) through the delivery point plus a look-ahead. The look-ahead
/// covers every failure mode's warning period, so a unit's vibration already
/// rises before the failure that the engine will produce, exactly as it does in
/// the offline preview. Only rows and samples at or before now are delivered:
/// the future is never written.
///
/// Snapshots keep each cycle's cost independent of the plant's age. Once rows
/// and tags are delivered past a whole hour of plant time, the engine state at
/// that hour is saved in Postgres and later cycles start from it. A catch-up
/// after sleep, power loss or a long stop advances at most BackfillSliceHours
/// of plant time per cycle, so it is bounded too. A snapshot that cannot be
/// used (another engine version, damaged, ahead of delivery) is ignored and the
/// plant replays from its start: slower, never different.
///
/// Rows go to Postgres through PlantStore (exactly-once effect). Tag samples go
/// to Historian in bounded batches. The publish intent is recorded before each
/// send and cleared after; if the host dies in between (power loss, kill), the
/// next start sends that batch again. Plant tags are deterministic, so a resent
/// point is the same tag, time and value.
/// </summary>
public sealed class PlantHost
{
    private const int MaximumPointsPerBatch = 1000;
    internal const double BackfillSliceHours = 6;
    private readonly PlantStore store;
    private readonly PlantModel model;
    private readonly HistorianClient? historian;
    private readonly string dataset;
    private PlantSnapshot? snapshot;
    private bool snapshotLoaded;

    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Receives (event, message) for snapshot conditions worth a log line.</summary>
    public Action<string, string>? Notice { get; set; }

    public PlantHost(PlantStore store, PlantModel model, HistorianClient? historian, string dataset)
    {
        this.store = store;
        this.model = model;
        this.historian = historian;
        this.dataset = dataset;
    }

    /// <summary>Hours of engine look-ahead: the longest warning period plus a margin.</summary>
    internal double LookaheadHours => model.Maintenance.FailureModes.Select(m => m.WarningHours).DefaultIfEmpty(0).Max() + 1;

    /// <summary>The snapshot the next cycle starts from; null when it replays from the plant start.</summary>
    internal PlantSnapshot? Snapshot => snapshot;

    /// <summary>
    /// One cycle: run the engine, apply due rows, publish due samples, save a
    /// snapshot when delivery passed a new hour. With maximumBatches set, stops
    /// after that many Historian batches so a long backfill yields regularly to
    /// cancellation and status.
    /// </summary>
    public async Task<PlantCycle> CycleAsync(int maximumBatches, CancellationToken stop)
    {
        var now = WholeSeconds(UtcNow());
        if (model.EndUtc is { } end && now > end) now = end;
        var registration = store.Registration(model.Simulation)
            ?? throw new RuntimeFailure("plant.not_admitted", "This simulation is not registered in flow_data_sim. Run plant build first.");
        var interval = TimeSpan.FromMilliseconds(model.SampleIntervalMs);
        // The last instant whose tags are in Historian (rows are always at least this far).
        DateTimeOffset? tagsDelivered = historian is null ? null : registration.TagCursorUtc ?? model.StartUtc - interval;
        if (!snapshotLoaded)
        {
            var (stored, ignored) = store.LoadSnapshot(registration, tagsDelivered is { } d ? d + interval : null);
            snapshot = stored;
            snapshotLoaded = true;
            if (ignored is not null) Notice?.Invoke("plant.snapshot_ignored", $"The stored snapshot was not used because {ignored}. Replaying from the plant start instead; the history is the same.");
            else if (stored is not null) Notice?.Invoke("plant.snapshot_resumed", $"Resuming from the snapshot at {stored.AtUtc:yyyy-MM-ddTHH:mm:ssZ}.");
        }

        var from = snapshot?.AtUtc ?? model.StartUtc;
        // Deliver at most one slice of plant time per cycle, so catching up after
        // a long gap never builds one huge run.
        var through = Min(now, from.AddHours(BackfillSliceHours));
        var horizon = through.AddHours(LookaheadHours);
        if (model.EndUtc is { } stopAt && horizon > stopAt) horizon = stopAt;
        if (horizon <= from) horizon = from.AddSeconds(1);
        // Snapshot at the last whole hour already delivered in both stores.
        var candidate = FloorHour(Min(through, tagsDelivered ?? through));
        DateTimeOffset? capture = candidate > from && candidate < horizon ? candidate : null;

        PlantRunResult result;
        try { result = PlantEngine.Run(model, horizon, snapshot, capture); }
        catch (PlantSnapshotException error)
        {
            Notice?.Invoke("plant.snapshot_ignored", $"The stored snapshot was not used because {error.Message} Replaying from the plant start instead; the history is the same.");
            snapshot = null;
            return await CycleAsync(maximumBatches, stop);
        }
        var run = result.Run;
        int rows = store.ApplyChanges(model, run.Changes, through);

        int published = 0, batches = 0;
        var next = through.AddTicks(1);
        if (historian is not null)
        {
            next = registration.TagCursorUtc is { } cursor ? cursor + interval : model.StartUtc;
            var signals = new PlantSignals(run);
            while (next <= through && batches < maximumBatches && !stop.IsCancellationRequested)
            {
                // Whole sample instants per batch, so every tag advances together.
                var data = new Dictionary<string, List<TvqPoint>>(StringComparer.Ordinal);
                int points = 0;
                var last = next;
                for (var t = next; t <= through; t += interval)
                {
                    foreach (var tag in signals.Tags)
                    {
                        if (signals.ValueAt(tag, t) is not { } value) continue;
                        if (!data.TryGetValue(tag.Name, out var list)) data[tag.Name] = list = [];
                        list.Add(new TvqPoint(t.UtcDateTime, JsonSerializer.SerializeToElement(value), 192));
                        points++;
                    }
                    last = t;
                    if (points >= MaximumPointsPerBatch - signals.Tags.Count) break;
                }
                // Once the intent is recorded the batch is always finished: cancellation
                // only stops the next batch, so a graceful stop never leaves one open.
                store.BeginSending(model.Simulation, last);
                await historian.Authenticate(CancellationToken.None);
                await historian.Publish(dataset, JsonSerializer.Serialize(data), CancellationToken.None);
                store.CompleteSending(model.Simulation, last);
                published += points;
                batches++;
                next = last + interval;
            }
        }

        // Rows are applied through `through` and tags were delivered past the
        // capture instant before this cycle began, so the snapshot only
        // describes history that is already durable in both stores.
        if (result.Snapshot is { } captured)
        {
            store.SaveSnapshot(registration, captured);
            snapshot = captured;
        }
        return new(through, rows, published, batches, through >= now && next > through);
    }

    /// <summary>Runs cycles until cancelled. Returns after the cancellation's in-flight batch completes.</summary>
    public async Task RunAsync(Action<PlantCycle> report, CancellationToken stop)
    {
        if (store.ResendInterruptedBatch(model.Simulation))
            report(new(UtcNow(), 0, 0, -1, false)); // -1 batches: an interrupted batch will be sent again
        while (!stop.IsCancellationRequested)
        {
            var cycle = await CycleAsync(50, stop);
            report(cycle);
            if (model.EndUtc is { } end && cycle.ThroughUtc >= end && cycle.CaughtUp) return;
            if (cycle.CaughtUp)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1000, model.SampleIntervalMs)), stop); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset FloorHour(DateTimeOffset utc) =>
        new(utc.UtcTicks - utc.UtcTicks % TimeSpan.TicksPerHour, TimeSpan.Zero);

    private static DateTimeOffset WholeSeconds(DateTimeOffset utc) =>
        new(utc.UtcTicks - utc.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
}
