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
