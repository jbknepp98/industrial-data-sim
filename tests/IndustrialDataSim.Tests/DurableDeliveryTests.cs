using System.Text.Json.Nodes;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Tests;

public class DurableDeliveryTests
{
    private static readonly RuntimeLimits Small = new() { BatchPoints = 3 };

    [Theory]
    [InlineData(FakeWriteBehavior.FailBeforeAcceptance, 0)]
    [InlineData(FakeWriteBehavior.LoseResponseAfterAcceptance, 1)]
    [InlineData(FakeWriteBehavior.PartialAcceptance, 1)]
    [InlineData(FakeWriteBehavior.AmbiguousResponse, 1)]
    public async Task AmbiguityStopsOnlyAffectedSessionAndNeverReplays(FakeWriteBehavior behavior, int firstTagPoints)
    {
        using var files = new RuntimeFixture();
        var fake = new FakeHistorian();
        string profile, dataset;
        using (var runtime = new DurableRuntime(files.Database, Small))
        {
            var model = RuntimeFixture.Model();
            profile = model["session"]!["connectionProfile"]!.GetValue<string>();
            dataset = model["session"]!["dataset"]!.GetValue<string>();
            runtime.AddSession(model.ToJsonString());
            runtime.AddSession(RuntimeFixture.Model("session-b", "B").ToJsonString());
            runtime.GenerateRound();
            fake.NextWrite("session-a", behavior);
            var delivery = new SimulatedDelivery(runtime, fake);
            Assert.Equal(1, await delivery.RunRoundAsync());
            Assert.Equal(SessionStatus.Uncertain, runtime.GetSession("session-a").Status);
            Assert.Equal(BatchStatus.Acknowledged, runtime.Batches("session-b")[0].Status);
            Assert.Equal(firstTagPoints, fake.Accepted(profile, dataset, "A.0").Count);
            Assert.All(runtime.Progress("session-a"), p => Assert.Null(p.AcknowledgedTicks));
            Assert.Throws<RuntimeFailure>(() => runtime.Resume("session-a"));
            Assert.Throws<RuntimeFailure>(() => runtime.ReleaseCompleted("session-a"));
        }
        using var reopened = new DurableRuntime(files.Database, Small);
        Assert.False(await new SimulatedDelivery(reopened, fake).DeliverOneAsync("session-a"));
        Assert.Equal(1, fake.Calls("session-a"));
        Assert.NotNull(reopened.Batches("session-a")[0].Payload);
        Assert.False(reopened.Generate("session-a").Progressed);
    }

    [Theory]
    [InlineData("before_sending_commit", BatchStatus.Pending, 0)]
    [InlineData("after_sending_commit", BatchStatus.Uncertain, 0)]
    [InlineData("before_acknowledgement_commit", BatchStatus.Uncertain, 1)]
    [InlineData("after_acknowledgement_commit", BatchStatus.Acknowledged, 1)]
    public async Task DeliveryCrashBoundariesRecoverConservatively(string boundary, BatchStatus expected, int calls)
    {
        using var files = new RuntimeFixture();
        var fake = new FakeHistorian();
        using (var runtime = new DurableRuntime(files.Database, Small))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a");
            runtime.FaultPoint = stage => { if (stage == boundary) throw new SimulatedCrash(); };
            await Assert.ThrowsAsync<SimulatedCrash>(() => new SimulatedDelivery(runtime, fake).DeliverOneAsync("session-a"));
        }
        using var reopened = new DurableRuntime(files.Database, Small);
        Assert.Equal(expected, reopened.Batches("session-a")[0].Status);
        Assert.Equal(calls, fake.Calls("session-a"));
        bool delivered = await new SimulatedDelivery(reopened, fake).DeliverOneAsync("session-a");
        Assert.Equal(expected == BatchStatus.Pending, delivered);
        Assert.Equal(expected == BatchStatus.Pending ? calls + 1 : calls, fake.Calls("session-a"));
    }

    [Fact]
    public async Task CrashAfterServerAcceptanceCannotBecomeAReplay()
    {
        using var files = new RuntimeFixture();
        var fake = new FakeHistorian();
        using (var runtime = new DurableRuntime(files.Database, Small))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a");
            var delivery = new SimulatedDelivery(runtime, fake) { FaultPoint = _ => throw new SimulatedCrash() };
            await Assert.ThrowsAsync<SimulatedCrash>(() => delivery.DeliverOneAsync("session-a"));
        }
        using var reopened = new DurableRuntime(files.Database, Small);
        Assert.Equal(SessionStatus.Uncertain, reopened.GetSession("session-a").Status);
        Assert.Contains("Do not replay", reopened.GetSession("session-a").ErrorMessage);
        Assert.False(await new SimulatedDelivery(reopened, fake).DeliverOneAsync("session-a"));
        Assert.Equal(1, fake.Calls("session-a"));
    }

    [Fact]
    public async Task RepeatsAdvanceAcknowledgementPastLatestRetainedAndReleasePreservesHistory()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, Small);
        var model = RuntimeFixture.Model();
        runtime.AddSession(model.ToJsonString());
        var fake = new FakeHistorian();
        var delivery = new SimulatedDelivery(runtime, fake);
        for (int turn = 0; turn < 10 && runtime.GetSession("session-a").Status != SessionStatus.Complete; turn++)
        {
            runtime.GenerateRound();
            await delivery.RunRoundAsync();
        }
        Assert.Equal(SessionStatus.Complete, runtime.GetSession("session-a").Status);
        string profile = model["session"]!["connectionProfile"]!.GetValue<string>();
        string dataset = model["session"]!["dataset"]!.GetValue<string>();
        Assert.Equal(3, fake.Accepted(profile, dataset, "A.0").Count);
        Assert.Single(fake.Retained(profile, dataset, "A.0"));
        var progress = runtime.Progress("session-a")[0];
        Assert.True(progress.AcknowledgedTicks > fake.Retained(profile, dataset, "A.0")[0].Timestamp.Ticks);
        Assert.Equal(progress.BufferedTicks, progress.AcknowledgedTicks);
        Assert.Equal(progress.SubmittedTicks, progress.AcknowledgedTicks);
        Assert.All(runtime.Batches("session-a"), b => { Assert.Null(b.Payload); Assert.Equal(BatchStatus.Acknowledged, b.Status); });
        Assert.Equal(0, runtime.GetSession("session-a").QueuedBytes);
        runtime.ReleaseCompleted("session-a");
        Assert.Equal("runtime.backward_range", Assert.Throws<RuntimeFailure>(() => runtime.AddSession(RuntimeFixture.Model("backward").ToJsonString())).Error.Code);
        var later = RuntimeFixture.Model("later");
        later["session"]!["startUtc"] = "2026-09-02T00:00:00Z";
        later["session"]!["endUtc"] = "2026-09-02T00:00:03Z";
        runtime.AddSession(later.ToJsonString());
    }

    [Fact]
    public async Task SameSessionCannotHaveTwoInFlightBatchesButOtherSessionCanProceed()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, Small);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.AddSession(RuntimeFixture.Model("session-b", "B").ToJsonString());
        runtime.GenerateRound();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeHistorian { BeforeWrite = () => { entered.SetResult(); return release.Task; } };
        var delivery = new SimulatedDelivery(runtime, fake);
        var first = delivery.DeliverOneAsync("session-a");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await delivery.DeliverOneAsync("session-a"));
        fake.BeforeWrite = null;
        Assert.True(await delivery.DeliverOneAsync("session-b"));
        release.SetResult();
        Assert.True(await first);
        Assert.Equal(1, fake.Calls("session-a"));
    }

    [Fact]
    public async Task CancellationBeforeClaimIsSafeButAfterClaimIsUncertain()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, Small);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.GenerateRound();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var fake = new FakeHistorian();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SimulatedDelivery(runtime, fake).DeliverOneAsync("session-a", cancellation.Token));
        Assert.Equal(BatchStatus.Pending, runtime.Batches("session-a")[0].Status);
        using var later = new CancellationTokenSource();
        fake.BeforeWrite = () => { later.Cancel(); return Task.CompletedTask; };
        Assert.False(await new SimulatedDelivery(runtime, fake).DeliverOneAsync("session-a", later.Token));
        Assert.Equal(SessionStatus.Uncertain, runtime.GetSession("session-a").Status);
    }

    [Fact]
    public async Task RoundRobinMakesProgressWithSpaceForOnlyOneSessionBatch()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 3, SessionQueuePoints = 3, GlobalQueuePoints = 3 });
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.AddSession(RuntimeFixture.Model("session-b", "B").ToJsonString());
        var delivery = new SimulatedDelivery(runtime, new());
        runtime.GenerateRound();
        Assert.Equal(3, runtime.GetSession("session-a").NextSlot);
        Assert.Equal(0, runtime.GetSession("session-b").NextSlot);
        await delivery.RunRoundAsync();
        runtime.GenerateRound();
        Assert.Equal(3, runtime.GetSession("session-b").NextSlot);
        for (int i = 0; i < 10; i++) { await delivery.RunRoundAsync(); runtime.GenerateRound(); }
        Assert.All(runtime.Sessions(), s => Assert.Equal(SessionStatus.Complete, s.Status));
    }

    [Fact]
    public async Task PauseDuringLastSubmissionCanResumeToCompletion()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.GenerateRound();
        var fake = new FakeHistorian { BeforeWrite = () => { runtime.Pause("session-a"); return Task.CompletedTask; } };
        await new SimulatedDelivery(runtime, fake).RunRoundAsync();
        Assert.Equal(SessionStatus.Paused, runtime.GetSession("session-a").Status);
        runtime.Resume("session-a");
        Assert.Equal(SessionStatus.Complete, runtime.GetSession("session-a").Status);
    }
}
