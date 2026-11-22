using System.Collections.Concurrent;
using IndustrialDataSim.Core.Plant;
using IndustrialDataSim.Runtime;
using Microsoft.Extensions.Hosting;

namespace IndustrialDataSim.Web;

/// <summary>Hosting states shown on the Plants tab and in /api/plants.</summary>
public static class HostingState
{
    /// <summary>Taking the lock and loading the stored model.</summary>
    public const string Starting = "starting";
    /// <summary>Catching up from the plant start (or the last snapshot) to now.</summary>
    public const string Backfilling = "backfilling";
    /// <summary>Caught up; delivering in real time.</summary>
    public const string Live = "live";
    /// <summary>Stopped from the page, or the plant reached its end time.</summary>
    public const string Stopped = "stopped";
    /// <summary>The host failed; it is retried with backoff. Code and message say why.</summary>
    public const string Error = "error";
    /// <summary>Another process holds the simulation's host lock (for example its own plant container). Not touched.</summary>
    public const string RunningElsewhere = "running_elsewhere";
    /// <summary>A plant in flow_data_sim that this container does not host and nobody is delivering.</summary>
    public const string NotHosted = "not_hosted";
}

public sealed record PlantHostingStatus(string State, DateTimeOffset SinceUtc, string? Code = null, string? Message = null,
    DateTimeOffset? ThroughUtc = null, long RowsApplied = 0, long PointsPublished = 0, int Failures = 0, DateTimeOffset? NextRetryUtc = null);

public sealed class PlantHostingOptions
{
    /// <summary>First retry delay after a host failure; doubled per consecutive failure up to <see cref="MaximumBackoff"/>.</summary>
    public TimeSpan FirstBackoff { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaximumBackoff { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>How often a plant held by another process is checked again.</summary>
    public TimeSpan ElsewhereRecheck { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>How often a registry read that failed at startup (database down) is retried.</summary>
    public TimeSpan RegistryRetry { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Runs every plant approved in this container, in this process (owner
/// decision: no Docker socket). One PlantHost per plant, each on its own task:
///
/// - Startup: plants in flowsim_web_plants with hosting enabled are started.
/// - Ownership: each host takes the simulation's advisory lock first. A plant
///   whose lock another process holds (for example the bluebirdbev-plant
///   container) is shown as running elsewhere, checked again periodically,
///   and never delivered from here, so two hosts can never write one plant.
/// - Failures: a host failure is caught in that plant's task, shown with its
///   error code and message, and retried with doubling backoff. Other plants
///   are unaffected.
/// - Shutdown (SIGINT/SIGTERM): every host is cancelled; PlantHost finishes
///   its in-flight Historian batch before returning, so stops are clean.
///   WebApp sets the host shutdown timeout to 150 s.
/// - Restart: PlantHost resumes from the plant's snapshot in Postgres
///   (plant.snapshot_resumed in the log) and re-sends a batch that was in
///   flight when the process died.
/// </summary>
public sealed class PlantHostingService : IHostedService
{
    private sealed class Slot(string simulation)
    {
        public string Simulation { get; } = simulation;
        public CancellationTokenSource Stop { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
    }

    private readonly PlantStore store;
    private readonly IWebPlantRegistry registry;
    private readonly Func<string, HistorianClient?> historianFactory;
    private readonly string? dataset;
    private readonly JsonLog log;
    private readonly PlantHostingOptions options;
    private readonly ConcurrentDictionary<string, Slot> slots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PlantHostingStatus> statuses = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource shutdown = new();
    private readonly object gate = new();
    private Task startup = Task.CompletedTask;

    /// <param name="historianFactory">Creates the Historian client for one plant; returns null to deliver rows only (tests).</param>
    /// <param name="dataset">TIMEBASE_DATASET: only for plants approved before each plant had its own Dataset.</param>
    public PlantHostingService(PlantStore store, IWebPlantRegistry registry, Func<string, HistorianClient?> historianFactory,
        string? dataset, JsonLog log, PlantHostingOptions? options = null)
    {
        this.store = store;
        this.registry = registry;
        this.historianFactory = historianFactory;
        this.dataset = dataset;
        this.log = log;
        this.options = options ?? new PlantHostingOptions();
    }

    /// <summary>Hosting status by simulation, for plants this container hosts or tried to host.</summary>
    public PlantHostingStatus? Status(string simulation) => statuses.TryGetValue(simulation, out var status) ? status : null;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Reading the registry needs Postgres; if it is down at startup, keep
        // trying in the background instead of failing the whole page.
        startup = Task.Run(async () =>
        {
            while (!shutdown.IsCancellationRequested)
            {
                try
                {
                    foreach (var entry in registry.List())
                    {
                        if (entry.HostingEnabled) StartPlant(entry.Simulation);
                        else statuses[entry.Simulation] = new(HostingState.Stopped, DateTimeOffset.UtcNow, Message: "Stopped from the page. Start it on the Plants tab.");
                    }
                    return;
                }
                catch (RuntimeFailure failure)
                {
                    log.Write("hosting.registry_unavailable", null, new { code = failure.Error.Code, message = failure.Message + " Retrying." });
                    try { await Task.Delay(options.RegistryRetry, shutdown.Token); } catch (OperationCanceledException) { return; }
                }
            }
        });
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        shutdown.Cancel();
        List<Slot> running;
        lock (gate) running = slots.Values.ToList();
        foreach (var slot in running) slot.Stop.Cancel();
        log.Write("hosting.stopping", null, new { plants = running.Count, message = "Stopping hosted plants; each finishes its in-flight batch first." });
        try { await Task.WhenAll(running.Select(s => s.Task).Append(startup)).WaitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            log.Write("hosting.stop_timeout", null, new { message = "The shutdown timeout passed before every plant stopped. A batch that was in flight is sent again at the next start." });
        }
    }

    /// <summary>Starts hosting the plant unless it is already running here. Returns immediately.</summary>
    public void StartPlant(string simulation)
    {
        lock (gate)
        {
            if (shutdown.IsCancellationRequested) return;
            if (slots.TryGetValue(simulation, out var existing) && !existing.Task.IsCompleted) return;
            var slot = new Slot(simulation);
            slots[simulation] = slot;
            statuses[simulation] = new(HostingState.Starting, DateTimeOffset.UtcNow);
            slot.Task = Task.Run(() => Supervise(slot));
        }
    }

    /// <summary>Stops the plant's host and waits until its in-flight batch has finished.</summary>
    public async Task StopPlant(string simulation)
    {
        Slot? slot;
        lock (gate) slots.TryGetValue(simulation, out slot);
        if (slot is null) return;
        slot.Stop.Cancel();
        await slot.Task;
        lock (gate) if (slots.TryGetValue(simulation, out var current) && current == slot) slots.TryRemove(simulation, out _);
    }

    /// <summary>Forgets a removed plant's status.</summary>
    public void Forget(string simulation) => statuses.TryRemove(simulation, out _);

    /// <summary>One plant's life: lock, run, and retry with backoff after failures. Never throws.</summary>
    private async Task Supervise(Slot slot)
    {
        string name = slot.Simulation;
        var stop = slot.Stop.Token;
        int failures = 0;
        while (!stop.IsCancellationRequested)
        {
            IDisposable? hostLock = null;
            HistorianClient? historian = null;
            try
            {
                try { hostLock = store.LockSimulation(name); }
                catch (RuntimeFailure failure) when (failure.Error.Code == "plant.host_running")
                {
                    if (Status(name)?.State != HostingState.RunningElsewhere)
                        log.Write("plant.running_elsewhere", name, new { message = "Another process is delivering this plant, so this container leaves it alone and checks again later." });
                    Set(name, new(HostingState.RunningElsewhere, DateTimeOffset.UtcNow, Message: "Another process holds this plant's host lock (for example its own plant container). This container does not touch it."));
                    await Task.Delay(options.ElsewhereRecheck, stop);
                    continue;
                }

                var model = LoadRegistered(name);
                // A plant approved here writes to its own Dataset; one approved
                // before that existed writes to TIMEBASE_DATASET, as it always did.
                string? target = registry.Find(name)?.Dataset ?? dataset;
                if (historianFactory(name) is { } client)
                {
                    historian = client;
                    if (string.IsNullOrWhiteSpace(target))
                        throw new RuntimeFailure("hosting.dataset_missing", "This plant has no Dataset of its own recorded, and TIMEBASE_DATASET is not set. Set TIMEBASE_DATASET to the Dataset it wrote to before, then restart the container.");
                }
                var host = new PlantHost(store, model, historian, target ?? "")
                {
                    Notice = (code, message) => log.Write(code, name, new { message })
                };
                Set(name, new(HostingState.Starting, DateTimeOffset.UtcNow));
                log.Write("plant.started", name, new { dataset = historian is null ? null : target, startUtc = model.StartUtc, lookaheadHours = host.LookaheadHours });
                if (historian is not null) await WarnAboutRetention(name, model, historian, target!);

                long rows = 0, points = 0;
                bool? caughtUp = null;
                var lastReport = DateTimeOffset.MinValue;
                await host.RunAsync(cycle =>
                {
                    if (cycle.Batches < 0)
                    {
                        log.Write("plant.interrupted_batch_resent", name, new { message = "The tag batch in flight when the plant last stopped unexpectedly is being sent again. Plant tags are deterministic, so resent points are identical." });
                        return;
                    }
                    rows += cycle.RowsApplied;
                    points += cycle.PointsPublished;
                    failures = 0;
                    Set(name, new(cycle.CaughtUp ? HostingState.Live : HostingState.Backfilling,
                        cycle.CaughtUp == caughtUp ? Status(name)!.SinceUtc : DateTimeOffset.UtcNow, ThroughUtc: cycle.ThroughUtc, RowsApplied: rows, PointsPublished: points));
                    // A log line when the condition changes, and backfill progress every 10 minutes.
                    if (cycle.CaughtUp != caughtUp || DateTimeOffset.UtcNow - lastReport > TimeSpan.FromMinutes(10))
                    {
                        log.Write(cycle.CaughtUp ? "plant.live" : "plant.backfilling", name, new { throughUtc = cycle.ThroughUtc, rowsApplied = rows, pointsPublished = points });
                        caughtUp = cycle.CaughtUp;
                        lastReport = DateTimeOffset.UtcNow;
                    }
                }, stop);

                bool requested = stop.IsCancellationRequested;
                Set(name, new(HostingState.Stopped, DateTimeOffset.UtcNow, Message: requested
                    ? "Stopped. Start it again to continue exactly where it left off."
                    : "The plant reached its end time and stopped.", RowsApplied: rows, PointsPublished: points));
                log.Write("plant.stopped", name, new { rowsApplied = rows, pointsPublished = points, reason = requested ? "requested" : "end_time" });
                return;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                Set(name, new(HostingState.Stopped, DateTimeOffset.UtcNow, Message: "Stopped. Start it again to continue exactly where it left off."));
                return;
            }
            catch (Exception error)
            {
                failures++;
                var (code, message) = error is RuntimeFailure failure
                    ? (failure.Error.Code, failure.Message)
                    // Raw exception text can carry connection details; only the type is logged.
                    : ("hosting.unexpected", $"The plant host failed unexpectedly ({error.GetType().Name}). It is retried automatically; if it keeps failing, check the database and Historian, then the container log.");
                var delay = Backoff(failures);
                Set(name, new(HostingState.Error, DateTimeOffset.UtcNow, code, message, Failures: failures, NextRetryUtc: DateTimeOffset.UtcNow + delay));
                log.Write("plant.error", name, new { code, message, failures, retryInSeconds = (int)delay.TotalSeconds });
                try { await Task.Delay(delay, stop); }
                catch (OperationCanceledException)
                {
                    Set(name, Status(name)! with { State = HostingState.Stopped, Message = "Stopped while waiting to retry: " + message });
                    return;
                }
            }
            finally
            {
                historian?.Dispose();
                hostLock?.Dispose();
            }
        }
        if (Status(name)?.State is not HostingState.Stopped)
            Set(name, new(HostingState.Stopped, DateTimeOffset.UtcNow, Message: "Stopped. Start it again to continue exactly where it left off."));
    }

    private TimeSpan Backoff(int failures)
    {
        double seconds = options.FirstBackoff.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 16));
        return TimeSpan.FromSeconds(Math.Min(seconds, options.MaximumBackoff.TotalSeconds));
    }

    private void Set(string simulation, PlantHostingStatus status) => statuses[simulation] = status;

    /// <summary>The stored model at its stored anchor reproduces exactly the history already written.</summary>
    private PlantModel LoadRegistered(string simulation)
    {
        var registration = store.Registration(simulation)
            ?? throw new RuntimeFailure("plant.not_admitted", "This plant is no longer registered in flow_data_sim (it may have been removed). Remove it from this page.");
        var loaded = PlantModelLoader.Load(registration.ModelJson, registration.AnchorUtc);
        if (!loaded.IsValid)
            throw new RuntimeFailure("plant.stored_model_invalid", "The stored model no longer validates with this version of the simulator. Use the version that admitted it, or remove the plant and approve it again.");
        return loaded.Model!;
    }

    /// <summary>Historian drops points older than the Dataset's purge age; say so up front, as plant run does.</summary>
    private async Task WarnAboutRetention(string name, PlantModel model, HistorianClient historian, string target)
    {
        await historian.Authenticate(CancellationToken.None);
        var settings = await historian.ReadDatasetSettings(target, CancellationToken.None);
        if (settings.PurgeAgeDays > 0 && model.StartUtc < DateTimeOffset.UtcNow.AddDays(-settings.PurgeAgeDays))
            log.Write("plant.retention_warning", name, new
            {
                purgeAgeDays = settings.PurgeAgeDays,
                message = "The backfill starts before this Dataset's purge age, so Historian will drop the oldest tag points. Postgres keeps every row."
            });
    }
}
