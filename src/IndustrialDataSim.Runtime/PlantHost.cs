using System.Text.Json;
using IndustrialDataSim.Core.Plant;
using IndustrialDataSim.Core.Simulation;

namespace IndustrialDataSim.Runtime;

/// <summary>Counters for one host cycle, reported to the caller and the log.</summary>
public sealed record PlantCycle(DateTimeOffset ThroughUtc, int RowsApplied, int PointsPublished, int Batches, bool CaughtUp);

/// <summary>
/// Runs an admitted plant live: backfill first, then real time, until stopped.
///
/// Each cycle replays the deterministic engine from the plant start through
/// now plus a look-ahead. The look-ahead covers every failure mode's warning
/// period, so a unit's vibration already rises before the failure that the
/// engine will produce, exactly as it does in the offline preview. Only rows
/// and samples at or before now are delivered: the future is never written.
///
/// Rows go to Postgres through PlantStore (exactly-once effect). Tag samples go
/// to Historian in bounded batches. The publish intent is recorded before each
/// send and cleared after; if the host dies in between, the next start skips
/// that batch and counts it as uncertain rather than replaying it.
/// </summary>
public sealed class PlantHost
{
    private const int MaximumPointsPerBatch = 1000;
    private readonly PlantStore store;
    private readonly PlantModel model;
    private readonly HistorianClient? historian;
    private readonly string dataset;

    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    public PlantHost(PlantStore store, PlantModel model, HistorianClient? historian, string dataset)
    {
        this.store = store;
        this.model = model;
        this.historian = historian;
        this.dataset = dataset;
    }

    /// <summary>Hours of engine look-ahead: the longest warning period plus a margin.</summary>
    internal double LookaheadHours => model.Maintenance.FailureModes.Select(m => m.WarningHours).DefaultIfEmpty(0).Max() + 1;

    /// <summary>
    /// One cycle: replay the engine, apply due rows, publish due samples. With
    /// maximumBatches set, stops after that many Historian batches so a long
    /// backfill yields regularly to cancellation and status.
    /// </summary>
    public async Task<PlantCycle> CycleAsync(int maximumBatches, CancellationToken stop)
    {
        var now = WholeSeconds(UtcNow());
        if (model.EndUtc is { } end && now > end) now = end;
        var horizon = now.AddHours(LookaheadHours);
        if (model.EndUtc is { } stopAt && horizon > stopAt) horizon = stopAt;
        var run = PlantEngine.Run(model, horizon <= model.StartUtc ? model.StartUtc.AddSeconds(1) : horizon);
        int rows = store.ApplyChanges(model, run.Changes, now);
        if (historian is null) return new(now, rows, 0, 0, true);

        var registration = store.Registration(model.Simulation)!;
        var interval = TimeSpan.FromMilliseconds(model.SampleIntervalMs);
        var next = registration.TagCursorUtc is { } cursor ? cursor + interval : model.StartUtc;
        var signals = new PlantSignals(run);
        int published = 0, batches = 0;
        while (next <= now && batches < maximumBatches && !stop.IsCancellationRequested)
        {
            // Whole sample instants per batch, so every tag advances together.
            var data = new Dictionary<string, List<TvqPoint>>(StringComparer.Ordinal);
            int points = 0;
            var last = next;
            for (var t = next; t <= now; t += interval)
            {
                int before = points;
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
            // only stops the next batch, so a graceful stop never leaves one uncertain.
            store.BeginSending(model.Simulation, last);
            await historian.Authenticate(CancellationToken.None);
            var receipt = await historian.Publish(dataset, JsonSerializer.Serialize(data), CancellationToken.None);
            store.CompleteSending(model.Simulation, last);
            published += points;
            batches++;
            next = last + interval;
            _ = receipt; // Dropped points are visible in Historian; the plant never resends.
        }
        return new(now, rows, published, batches, next > now);
    }

    /// <summary>Runs cycles until cancelled. Returns after the cancellation's in-flight batch completes.</summary>
    public async Task RunAsync(Action<PlantCycle> report, CancellationToken stop)
    {
        if (store.SkipUncertainBatch(model.Simulation))
            report(new(UtcNow(), 0, 0, -1, false)); // -1 batches: an uncertain batch was skipped on start
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

    private static DateTimeOffset WholeSeconds(DateTimeOffset utc) =>
        new(utc.UtcTicks - utc.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
}
