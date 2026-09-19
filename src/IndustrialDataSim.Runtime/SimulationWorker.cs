using Microsoft.Extensions.Logging;

namespace IndustrialDataSim.Runtime;

public enum WorkerStopReason { Completed, Blocked, RoundLimit, Stopped }
public sealed record WorkerRunResult(WorkerStopReason StopReason, int RoundsStarted,
    int ProgressingGenerationTurns, int AcknowledgedBatches, string Message,
    IReadOnlyList<SessionSnapshot> Sessions);

/// <summary>
/// Finite simulation-only worker. One turn generates at most one bounded window
/// and delivers at most one batch for a session. No real-time pacing or network
/// transport is enabled. The host owns the runtime's lifetime and exclusive lock.
/// </summary>
public sealed class SimulationWorker
{
    private readonly DurableRuntime runtime;
    private readonly SimulatedDelivery delivery;
    private int running;
    private uint nextRound;

    public SimulationWorker(DurableRuntime runtime, FakeHistorian? historian = null)
    {
        this.runtime = runtime;
        // Full histories are useful in small tests, but must not grow with a
        // long worker run. An explicitly supplied test fake owns its own limits.
        delivery = new(runtime, historian ?? new FakeHistorian { KeepHistory = false });
    }

    public Task<WorkerRunResult> RunAsync(int maximumRounds, CancellationToken stop = default) =>
        RunCoreAsync(maximumRounds, stop, reportLifecycle: true);

    // Resident hosting reports transitions itself, rather than logging every round.
    internal Task<WorkerRunResult> RunRoundAsync(CancellationToken stop) =>
        RunCoreAsync(1, stop, reportLifecycle: false);

    private async Task<WorkerRunResult> RunCoreAsync(int maximumRounds, CancellationToken stop, bool reportLifecycle)
    {
        if (maximumRounds is < 1 or > 10000)
            throw new RuntimeFailure("worker.invalid_round_limit", "Choose a worker round limit from 1 through 10000. Inspect the outcome before starting another run.");
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new RuntimeFailure("worker.already_running", "This worker is already running. Request a graceful stop and wait for it to finish before starting another run.");
        int rounds = 0, generated = 0, acknowledged = 0;
        try
        {
            var sessions = Snapshot();
            if (reportLifecycle) runtime.WorkerEvent("worker.started", "Bounded simulation worker started.", "No production Historian writes are enabled. Request a graceful stop before using separate lifecycle commands.");
            while (true)
            {
                if (stop.IsCancellationRequested) return Finish(WorkerStopReason.Stopped, "Graceful stop completed. In-flight delivery has finished; pending work and checkpoints remain durable.");
                if (sessions.All(s => s.Status is SessionStatus.Complete or SessionStatus.Cancelled))
                    return Finish(WorkerStopReason.Completed, "All sessions are Complete or Cancelled; no unfinished sessions remain in this database.");
                if (rounds >= maximumRounds)
                    return Finish(WorkerStopReason.RoundLimit, "Round limit reached with unfinished sessions. Inspect status and continue with compatible runtime limits when ready.");
                bool progressed = false;
                // Rotate the first session each round without depending on another
                // session's random stream or model clock. Each session gets one turn.
                int start = (int)(nextRound++ % (uint)sessions.Count);
                rounds++;
                for (int offset = 0; offset < sessions.Count; offset++)
                {
                    var session = sessions[(start + offset) % sessions.Count];
                    // Terminal states cannot become runnable again. Retain them
                    // in inventory/cap checks, but avoid database generation and
                    // delivery probes for every historical session on each round.
                    if (session.Status is SessionStatus.Complete or SessionStatus.Cancelled) continue;
                    // The fake often completes synchronously. Yield between turns
                    // so an async host can request stop without a separate thread.
                    await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                    if (stop.IsCancellationRequested) return Finish(WorkerStopReason.Stopped, "Graceful stop completed. Pending work and checkpoints remain durable.");
                    string id = session.SessionId;
                    try
                    {
                        if (runtime.Generate(id).Progressed) { progressed = true; generated++; }
                    }
                    catch (RuntimeFailure failure) when (failure.Error.Code == "runtime.configuration_integrity")
                    {
                        // The runtime persisted Failed before throwing. Do not
                        // swallow storage errors or unexpected exceptions here.
                    }
                    if (stop.IsCancellationRequested) return Finish(WorkerStopReason.Stopped, "Graceful stop completed after generation. Newly queued work remains pending.");
                    // A graceful host stop must not manufacture uncertain delivery
                    // by cancelling a submission already in flight. Finish it first.
                    if (await delivery.DeliverOneAsync(id, CancellationToken.None)) { progressed = true; acknowledged++; }
                }
                sessions = Snapshot();
                if (!progressed && sessions.Any(s => s.Status is not (SessionStatus.Complete or SessionStatus.Cancelled)))
                    return Finish(stop.IsCancellationRequested ? WorkerStopReason.Stopped : WorkerStopReason.Blocked,
                        "No further progress was made. Inspect session errors, pause/cancellation state, queue limits, disk headroom, and runtime logs before continuing. Uncertain work must not be replayed.");
            }
        }
        finally { Volatile.Write(ref running, 0); }

        WorkerRunResult Finish(WorkerStopReason reason, string message)
        {
            var final = Snapshot();
            if (reportLifecycle) runtime.WorkerEvent("worker.stopped", $"Simulation worker stopped: {reason}.", message);
            return new(reason, rounds, generated, acknowledged, message, final);
        }
    }

    private IReadOnlyList<SessionSnapshot> Snapshot()
    {
        var page = runtime.ListSessions();
        if (page.Count == 100 && runtime.ListSessions(page[^1].SessionId, 1).Count > 0)
            throw new RuntimeFailure("worker.session_limit",
                "This initial worker supports at most 100 total sessions per database, including finished sessions. Use separate simulation databases with disjoint tags for larger workloads; do not delete recovery state to bypass the limit.");
        return page;
    }
}

public sealed partial class DurableRuntime
{
    internal void WorkerEvent(string code, string message, string action) => Access(() =>
    {
        Log(LogLevel.Information, code, "Worker", message, action);
        return true;
    });
}
