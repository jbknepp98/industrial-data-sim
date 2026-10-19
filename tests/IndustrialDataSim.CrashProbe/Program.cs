using IndustrialDataSim.Runtime;

// Test-only child process. The parent kills us at a durable boundary, so neither
// using/Dispose nor transaction rollback handlers can manufacture safe recovery.
if (args.Length != 3) return 2;
try
{
    void StopAt(string stage)
    {
        if (stage != args[2]) return;
        Console.WriteLine("boundary-reached");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
    }
    if (args[2].Contains("restore_", StringComparison.Ordinal))
    {
        using (var source = new DurableRuntime(args[0], new() { BatchPoints = 3 }))
        {
            source.AddSession(File.ReadAllText(args[1]));
            source.Generate("session-a");
        }
        string directory = Path.GetDirectoryName(args[0])!;
        var receipt = StateBackup.Create(args[0], directory);
        StateBackup.RestoreCore(Path.Combine(directory, receipt.BackupId + ".evidence.sqlite"), receipt.Sha256,
            receipt.SourceSchemaVersion, directory, StopAt);
        return 0;
    }
    using var runtime = new DurableRuntime(args[0], new() { BatchPoints = 3 });
    runtime.AddSession(File.ReadAllText(args[1]));
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
    if (args[2].Contains("archive_", StringComparison.Ordinal))
    {
        runtime.Generate("session-a");
        runtime.Pause("session-a");
        runtime.ArchiveHistory("session-a", Path.GetDirectoryName(args[0])!, 1);
    }
    return 0;
}
catch (RuntimeFailure error)
{
    Console.WriteLine(error.Error.Code);
    return 1;
}
