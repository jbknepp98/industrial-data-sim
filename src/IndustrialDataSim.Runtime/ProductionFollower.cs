namespace IndustrialDataSim.Runtime;

/// <summary>
/// Catch up from the durable cursor, then wait for the original sample grid to
/// become due. Restart uses the same model and database; no separate live session
/// or in-memory generator state is created at the backfill boundary.
/// </summary>
public sealed class ProductionFollower(DurableRuntime runtime, ProductionDelivery delivery, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private int running;

    public async Task<ProductionRunResult> FollowAsync(TimeSpan duration, CancellationToken stop = default)
    {
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromDays(7))
            throw new RuntimeFailure("worker.invalid_follow_duration", "Choose a foreground follow duration from 1 through 604800 seconds. Restart against the same database to continue; do not re-admit the session.");
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new RuntimeFailure("worker.already_running", "This production follower is already running. Stop it gracefully before starting another invocation.");
        try
        {
            var worker = new ProductionWorker(runtime, delivery);
            long started = clock.GetTimestamp();
            int rounds = 0, published = 0;
            while (true)
            {
                if (stop.IsCancellationRequested) return Result("Stopped");
                if (clock.GetElapsedTime(started) >= duration) return Result("DurationLimit");
                var result = await worker.RunAsync(1, stop, clock.GetUtcNow());
                rounds += result.Rounds;
                published += result.PublishedBatches;
                if (result.StopReason is "Completed" or "Stopped") return Result(result.StopReason);
                if (result.StopReason == "Blocked")
                {
                    // Paused/failed/uncertain-only state needs operator action.
                    // Ready with no due samples waits, including clock rollback.
                    if (!result.Sessions.Any(s => s.Status is SessionStatus.Ready or SessionStatus.Draining or SessionStatus.Cancelling))
                        return Result("Blocked");
                    try { await Task.Delay(TimeSpan.FromSeconds(1), clock, stop); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { return Result("Stopped"); }
                }
            }
            ProductionRunResult Result(string reason) => new(reason, rounds, published,
                "Wall-clock paced publishing stopped. Continue with production follow against the same database and unchanged model. Inspect errors and arrival observations; Uncertain work must never be replayed. Session endUtc remains the scheduled end.", runtime.ListSessions());
        }
        finally { Volatile.Write(ref running, 0); }
    }
}
