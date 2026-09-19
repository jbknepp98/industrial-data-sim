using System.Text.Json;
using IndustrialDataSim.Cli;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Tests;

public class SimulationWorkerTests
{
    [Fact]
    public async Task TwoSessionsMakeFairProgressWithOneBatchOfGlobalCapacity()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 3, SessionQueuePoints = 3, GlobalQueuePoints = 3 });
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.AddSession(RuntimeFixture.Model("session-b", "B").ToJsonString());
        var fake = new FakeHistorian();
        var worker = new SimulationWorker(runtime, fake);
        var first = await worker.RunAsync(1);
        Assert.Equal(WorkerStopReason.RoundLimit, first.StopReason);
        Assert.All(first.Sessions, session => Assert.Equal(3, session.NextSlot));
        Assert.Equal(2, first.AcknowledgedBatches);
        Assert.Equal(WorkerStopReason.Completed, (await worker.RunAsync(10)).StopReason);
        Assert.Equal(3, fake.Calls("session-a"));
        Assert.Equal(3, fake.Calls("session-b"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoProgressStopsInsteadOfSpinning(bool lowDisk)
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        if (lowDisk) runtime.FreeDiskBytes = () => 0;
        else runtime.Pause("session-a");
        var result = await new SimulationWorker(runtime).RunAsync(10000);
        Assert.Equal(WorkerStopReason.Blocked, result.StopReason);
        Assert.Equal(1, result.RoundsStarted);
        Assert.Equal(0, result.ProgressingGenerationTurns);
        Assert.Contains("Inspect", result.Message);
    }

    [Fact]
    public async Task UncertainSessionStopsWhileHealthySessionCompletesWithoutReplay()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 3 });
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.AddSession(RuntimeFixture.Model("session-b", "B").ToJsonString());
        var fake = new FakeHistorian();
        fake.NextWrite("session-a", FakeWriteBehavior.LoseResponseAfterAcceptance);
        var result = await new SimulationWorker(runtime, fake).RunAsync(10);
        Assert.Equal(WorkerStopReason.Blocked, result.StopReason);
        Assert.Equal(SessionStatus.Uncertain, runtime.GetSession("session-a").Status);
        Assert.Equal(SessionStatus.Complete, runtime.GetSession("session-b").Status);
        Assert.Equal(1, fake.Calls("session-a"));
        Assert.NotNull(Assert.Single(runtime.Batches("session-a")).Payload);
    }

    [Fact]
    public async Task GracefulStopLetsClaimedSubmissionFinishThenReleasesNoFurtherWork()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 3 });
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.AddSession(RuntimeFixture.Model("session-b", "B").ToJsonString());
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeHistorian { BeforeWrite = () => { entered.TrySetResult(); return release.Task; } };
        var worker = new SimulationWorker(runtime, fake);
        var run = worker.RunAsync(100, stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("worker.already_running", (await Assert.ThrowsAsync<RuntimeFailure>(() => worker.RunAsync(1))).Error.Code);
            stop.Cancel();
            Assert.False(run.IsCompleted);
        }
        finally { release.TrySetResult(); }
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkerStopReason.Stopped, result.StopReason);
        Assert.Equal(BatchStatus.Acknowledged, Assert.Single(runtime.Batches("session-a")).Status);
        Assert.Equal(0, runtime.GetSession("session-b").NextSlot);
        Assert.NotEqual(SessionStatus.Uncertain, runtime.GetSession("session-a").Status);
    }

    [Fact]
    public async Task PreCancelledRunDoesNotGenerateOrSubmit()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        var result = await new SimulationWorker(runtime).RunAsync(10, new CancellationToken(true));
        Assert.Equal(WorkerStopReason.Stopped, result.StopReason);
        Assert.Equal(0, result.RoundsStarted);
        Assert.Empty(runtime.Batches("session-a"));
    }

    [Fact]
    public async Task BoundedFakeRetainsOnlyLatestPointsAndStillRejectsBackwardWrites()
    {
        using var files = new RuntimeFixture();
        var model = RuntimeFixture.Model();
        string profile = model["session"]!["connectionProfile"]!.GetValue<string>();
        string dataset = model["session"]!["dataset"]!.GetValue<string>();
        using var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 3 });
        runtime.AddSession(model.ToJsonString());
        var fake = new FakeHistorian { KeepHistory = false };
        Assert.Equal(WorkerStopReason.Completed, (await new SimulationWorker(runtime, fake).RunAsync(10)).StopReason);
        Assert.Single(fake.Accepted(profile, dataset, "A.0"));
        Assert.Single(fake.Retained(profile, dataset, "A.0"));
        using var otherFiles = new RuntimeFixture();
        using var other = new DurableRuntime(otherFiles.Database);
        other.AddSession(model.ToJsonString());
        Assert.Equal(WorkerStopReason.Blocked, (await new SimulationWorker(other, fake).RunAsync(2)).StopReason);
        Assert.Equal(SessionStatus.Uncertain, other.GetSession("session-a").Status);
    }

    [Fact]
    public async Task DrainAndCancelledSessionsReachTerminalOutcome()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 3 });
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.AddSession(RuntimeFixture.Model("session-b", "B").ToJsonString());
        runtime.Generate("session-a");
        runtime.Cancel("session-a", CancellationMode.Drain);
        runtime.Cancel("session-b", CancellationMode.DiscardPending);
        var result = await new SimulationWorker(runtime).RunAsync(10);
        Assert.Equal(WorkerStopReason.Completed, result.StopReason);
        Assert.All(result.Sessions, s => Assert.Equal(SessionStatus.Cancelled, s.Status));
        Assert.Equal(0, result.ProgressingGenerationTurns);
        Assert.Equal(1, result.AcknowledgedBatches);
    }

    [Fact]
    public async Task UnexpectedFailurePropagatesAndDoesNotLeaveWorkerMarkedRunning()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.FaultPoint = stage => { if (stage == "before_generation_commit") throw new InvalidOperationException("test fault"); };
        var worker = new SimulationWorker(runtime);
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RunAsync(10));
        Assert.Equal(0, runtime.GetSession("session-a").NextSlot);
        runtime.FaultPoint = null;
        Assert.Equal(WorkerStopReason.Completed, (await worker.RunAsync(10)).StopReason);
    }

    [Fact]
    public void CliWorkerHonorsPreRequestedStopAndValidatesRoundLimits()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database)) runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        using var output = new StringWriter();
        Assert.Equal(130, CliApplication.Run(["session", "run-simulated", files.Database, "10"], output, new CancellationToken(true)));
        Assert.Equal("Stopped", JsonSerializer.Deserialize<JsonElement>(output.ToString()).GetProperty("result").GetProperty("stopReason").GetString());
        using var invalid = new StringWriter();
        Assert.Equal(2, CliApplication.Run(["session", "run-simulated", files.Database, "10001"], invalid));
        using var inspected = new DurableRuntime(files.Database);
        Assert.Empty(inspected.Batches("session-a"));
    }

    [Fact]
    public async Task SessionAndRoundLimitsRefuseWorkBeforeGeneration()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        for (int i = 0; i < 101; i++) runtime.AddSession(RuntimeFixture.Model($"session-{i}", $"T{i}").ToJsonString());
        var worker = new SimulationWorker(runtime);
        Assert.Equal("worker.invalid_round_limit", (await Assert.ThrowsAsync<RuntimeFailure>(() => worker.RunAsync(0))).Error.Code);
        Assert.Equal("worker.session_limit", (await Assert.ThrowsAsync<RuntimeFailure>(() => worker.RunAsync(1))).Error.Code);
        Assert.All(runtime.Sessions(), s => Assert.Equal(0, s.NextSlot));
    }

    [Fact]
    public void CliWorkerReportsRoundLimitThenCompletesAfterReopen()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database))
        {
            var model = RuntimeFixture.Model();
            model["session"]!["endUtc"] = "2026-09-01T00:10:00Z";
            runtime.AddSession(model.ToJsonString());
        }
        using var firstOutput = new StringWriter();
        Assert.Equal(4, CliApplication.Run(["session", "run-simulated", files.Database, "1"], firstOutput));
        Assert.Equal("RoundLimit", JsonSerializer.Deserialize<JsonElement>(firstOutput.ToString()).GetProperty("result").GetProperty("stopReason").GetString());
        using var finalOutput = new StringWriter();
        Assert.Equal(0, CliApplication.Run(["session", "run-simulated", files.Database, "10"], finalOutput));
        Assert.Equal("Completed", JsonSerializer.Deserialize<JsonElement>(finalOutput.ToString()).GetProperty("result").GetProperty("stopReason").GetString());
        using var inspected = new DurableRuntime(files.Database);
        Assert.Equal(1800, inspected.GetSession("session-a").NextSlot);
    }
}
