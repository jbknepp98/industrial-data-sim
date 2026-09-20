namespace IndustrialDataSim.Runtime;

public sealed record ProductionRunResult(string StopReason, int Rounds, int PublishedBatches,
    string Message, IReadOnlyList<SessionSnapshot> Sessions);

/// <summary>
/// Bounded foreground production worker. Each round gives each session one
/// generation window and one publish opportunity. Completed HTTP operations are
/// called Published, never Acknowledged. Graceful stop finishes in-flight work.
/// </summary>
public sealed class ProductionWorker(DurableRuntime runtime, ProductionDelivery delivery)
{
    private int running;
    public async Task<ProductionRunResult> RunAsync(int maximumRounds, CancellationToken stop = default)
    {
        runtime.RequireMode(ExecutionMode.Production);
        if (maximumRounds is < 1 or > 10000) throw new RuntimeFailure("worker.invalid_round_limit", "Choose 1–10000 production rounds. Inspect published progress and observations before continuing.");
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new RuntimeFailure("worker.already_running", "The production worker is already running. Wait for its current run to finish.");
        int rounds = 0, published = 0;
        try
        {
            while (true)
            {
                var sessions = runtime.ListSessions();
                if (sessions.Count == 100 && runtime.ListSessions(sessions[^1].SessionId, 1).Count > 0)
                    throw new RuntimeFailure("worker.session_limit", "This worker supports 100 unarchived sessions. Archive eligible finished history before another run; never delete unresolved work.");
                if (stop.IsCancellationRequested) return Result("Stopped");
                if (sessions.All(s => s.Status is SessionStatus.Complete or SessionStatus.Cancelled)) return Result("Completed");
                if (rounds >= maximumRounds) return Result("RoundLimit");
                int start = rounds++ % sessions.Count;
                bool progressed = false;
                for (int offset = 0; offset < sessions.Count; offset++)
                {
                    if (stop.IsCancellationRequested) return Result("Stopped");
                    var session = sessions[(start + offset) % sessions.Count];
                    if (session.Status is not (SessionStatus.Ready or SessionStatus.Draining or SessionStatus.Cancelling)) continue;
                    try { progressed |= runtime.Generate(session.SessionId).Progressed; }
                    catch (RuntimeFailure failure) when (failure.Error.Code == "runtime.configuration_integrity") { continue; }
                    if (stop.IsCancellationRequested) return Result("Stopped");
                    if (await delivery.DeliverOneAsync(session.SessionId, CancellationToken.None)) { published++; progressed = true; }
                }
                if (!progressed) return Result("Blocked");
            }
        }
        finally { Volatile.Write(ref running, 0); }

        ProductionRunResult Result(string reason) => new(reason, rounds, published,
            "Publishing and arrival observations are separate. Inspect session errors and observations, then review the intended pattern. Uncertain batches are never replayed. Stop preserves pending work and ownership.", runtime.ListSessions());
    }
}
