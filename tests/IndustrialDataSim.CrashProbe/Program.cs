using IndustrialDataSim.Runtime;

// Test-only child process. The parent kills us at a durable boundary, so neither
// using/Dispose nor transaction rollback handlers can manufacture safe recovery.
if (args.Length != 3) return 2;
try
{
    using var runtime = new DurableRuntime(args[0], new() { BatchPoints = 3 });
    runtime.AddSession(File.ReadAllText(args[1]));
    void StopAt(string stage)
    {
        if (stage != args[2]) return;
        Console.WriteLine("boundary-reached");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
    }
    runtime.FaultPoint = StopAt;
    if (args[2] is "before_horizon_commit" or "after_horizon_commit")
    {
        runtime.ExtendHorizon("session-a", DateTimeOffset.Parse("2026-09-01T00:00:06Z"), 0,
            "6886f6d2-d0e9-43d7-9406-1c31cb34cb54");
        return 0;
    }
    runtime.Generate("session-a");
    if (args[2] is "before_cancellation_commit" or "after_cancellation_commit")
    {
        runtime.Cancel("session-a", CancellationMode.DiscardPending);
        return 0;
    }
    var delivery = new SimulatedDelivery(runtime, new()) { FaultPoint = StopAt };
    await delivery.DeliverOneAsync("session-a");
    return 0;
}
catch (RuntimeFailure error)
{
    Console.WriteLine(error.Error.Code);
    return 1;
}
