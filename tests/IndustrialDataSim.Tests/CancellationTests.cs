using IndustrialDataSim.Runtime;
using Microsoft.Data.Sqlite;

namespace IndustrialDataSim.Tests;

public class CancellationTests
{
    [Fact]
    public async Task DrainStopsGenerationSurvivesRestartAndFinishesWithoutCompletingRange()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 3 }))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a");
            runtime.Pause("session-a");
            runtime.Cancel("session-a", CancellationMode.Drain);
            runtime.Cancel("session-a", CancellationMode.Drain);
            Assert.Equal(SessionStatus.Cancelling, runtime.GetSession("session-a").Status);
            Assert.False(runtime.Generate("session-a").Progressed);
            Assert.Throws<RuntimeFailure>(() => runtime.Resume("session-a"));
            Assert.Throws<RuntimeFailure>(() => runtime.ReleaseCancelled("session-a"));
        }
        using var reopened = new DurableRuntime(files.Database);
        Assert.Equal(CancellationMode.Drain, reopened.GetSession("session-a").Cancellation);
        Assert.Equal(1, await new SimulatedDelivery(reopened, new()).RunRoundAsync());
        var after = reopened.GetSession("session-a");
        Assert.Equal(SessionStatus.Cancelled, after.Status);
        Assert.Equal(3, after.NextSlot);
        Assert.Equal(9, after.TotalSlots);
        Assert.Equal(0, after.QueuedPoints);
        var progress = reopened.Progress("session-a").ToArray();
        reopened.ReleaseCancelled("session-a");
        Assert.Equal(progress, reopened.Progress("session-a"));
    }

    [Fact]
    public async Task DiscardReleasesCapacityButPreservesAuditProgressAndOrdering()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 3, SessionQueuePoints = 3, GlobalQueuePoints = 3 });
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.AddSession(RuntimeFixture.Model("session-b", "B").ToJsonString());
        runtime.Generate("session-a");
        var batch = Assert.Single(runtime.Batches("session-a"));
        var progress = runtime.Progress("session-a").ToArray();
        Assert.False(runtime.Generate("session-b").Progressed);
        runtime.Cancel("session-a", CancellationMode.DiscardPending);
        Assert.Equal(batch with { Status = BatchStatus.Discarded, Payload = null }, Assert.Single(runtime.Batches("session-a")));
        Assert.Equal(progress, runtime.Progress("session-a"));
        Assert.Equal(SessionStatus.Cancelled, runtime.GetSession("session-a").Status);
        Assert.Equal(0, runtime.GetSession("session-a").QueuedPoints);
        Assert.True(runtime.Generate("session-b").Progressed);
        var fake = new FakeHistorian();
        Assert.False(await new SimulatedDelivery(runtime, fake).DeliverOneAsync("session-a"));
        Assert.Equal(0, fake.Calls("session-a"));
        Assert.Equal("runtime.tag_owned", Assert.Throws<RuntimeFailure>(() => runtime.AddSession(RuntimeFixture.Model("conflict").ToJsonString())).Error.Code);
        runtime.ReleaseCancelled("session-a");
        Assert.Equal("runtime.backward_range", Assert.Throws<RuntimeFailure>(() => runtime.AddSession(RuntimeFixture.Model("earlier").ToJsonString())).Error.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DrainWaitsForInFlightOutcomeWithoutReleasingUncertainty(bool accepted)
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 3 });
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.Generate("session-a");
        var work = runtime.Claim("session-a")!;
        Assert.Equal("runtime.cancellation_unresolved", Assert.Throws<RuntimeFailure>(() => runtime.Cancel("session-a", CancellationMode.DiscardPending)).Error.Code);
        runtime.Cancel("session-a", CancellationMode.Drain);
        runtime.Finish(work, accepted);
        Assert.Equal(accepted ? SessionStatus.Cancelled : SessionStatus.Uncertain, runtime.GetSession("session-a").Status);
        if (!accepted)
        {
            Assert.Throws<RuntimeFailure>(() => runtime.Cancel("session-a", CancellationMode.DiscardPending));
            Assert.Throws<RuntimeFailure>(() => runtime.Cancel("session-a", CancellationMode.Drain));
            Assert.Throws<RuntimeFailure>(() => runtime.ReleaseCancelled("session-a"));
            Assert.NotNull(Assert.Single(runtime.Batches("session-a")).Payload);
        }
    }

    [Theory]
    [InlineData(CancellationMode.Drain)]
    [InlineData(CancellationMode.DiscardPending)]
    public void EmptySessionCancelsImmediatelyAndModeCannotChange(CancellationMode mode)
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.Cancel("session-a", mode);
        runtime.Cancel("session-a", mode);
        Assert.Equal(SessionStatus.Cancelled, runtime.GetSession("session-a").Status);
        var other = mode == CancellationMode.Drain ? CancellationMode.DiscardPending : CancellationMode.Drain;
        Assert.Equal("runtime.cancellation_locked", Assert.Throws<RuntimeFailure>(() => runtime.Cancel("session-a", other)).Error.Code);
        Assert.Throws<RuntimeFailure>(() => runtime.Resume("session-a"));
        Assert.Throws<RuntimeFailure>(() => runtime.Pause("session-a"));
    }

    [Theory]
    [InlineData("before_cancellation_commit", SessionStatus.Ready, BatchStatus.Pending)]
    [InlineData("after_cancellation_commit", SessionStatus.Cancelled, BatchStatus.Discarded)]
    public void DiscardAndCancellationCommitAtomically(string boundary, SessionStatus status, BatchStatus batchStatus)
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 3 }))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a");
            runtime.FaultPoint = stage => { if (stage == boundary) throw new SimulatedCrash(); };
            Assert.Throws<SimulatedCrash>(() => runtime.Cancel("session-a", CancellationMode.DiscardPending));
        }
        using var reopened = new DurableRuntime(files.Database);
        Assert.Equal(status, reopened.GetSession("session-a").Status);
        var batch = Assert.Single(reopened.Batches("session-a"));
        Assert.Equal(batchStatus, batch.Status);
        Assert.Equal(batchStatus == BatchStatus.Discarded, batch.Payload is null);
        Assert.Equal(status == SessionStatus.Cancelled ? 0 : 3, reopened.GetSession("session-a").QueuedPoints);
    }

    [Fact]
    public void FailedGenerationMayDiscardButCannotDrainOrEraseItsError()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, new() { BatchBytes = 128 });
        var model = RuntimeFixture.Model();
        model["generators"]![2]!["value"] = new string('<', 100);
        runtime.AddSession(model.ToJsonString());
        runtime.Generate("session-a");
        runtime.Generate("session-a");
        Assert.Equal(SessionStatus.Failed, runtime.GetSession("session-a").Status);
        Assert.Throws<RuntimeFailure>(() => runtime.Cancel("session-a", CancellationMode.Drain));
        runtime.Cancel("session-a", CancellationMode.DiscardPending);
        Assert.Equal("generation.point_too_large", runtime.GetSession("session-a").ErrorCode);
        Assert.Equal(SessionStatus.Cancelled, runtime.GetSession("session-a").Status);
        Assert.Throws<RuntimeFailure>(() => runtime.RetryGeneration("session-a"));
    }

    [Fact]
    public async Task CompleteAndUncertainSessionsRejectCancellationAndInvalidModesExplainChoices()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        var invalid = Assert.Throws<RuntimeFailure>(() => runtime.Cancel("session-a", (CancellationMode)99));
        Assert.Equal("runtime.invalid_cancellation_mode", invalid.Error.Code);
        Assert.Contains("DiscardPending", invalid.Message);
        Assert.Equal(SessionStatus.Ready, runtime.GetSession("session-a").Status);
        runtime.Generate("session-a");
        await new SimulatedDelivery(runtime, new()).DeliverOneAsync("session-a");
        Assert.Throws<RuntimeFailure>(() => runtime.Cancel("session-a", CancellationMode.Drain));
        runtime.AddSession(RuntimeFixture.Model("session-b", "B").ToJsonString());
        runtime.Generate("session-b");
        runtime.Finish(runtime.Claim("session-b")!, false);
        Assert.Throws<RuntimeFailure>(() => runtime.Cancel("session-b", CancellationMode.DiscardPending));
        Assert.Equal(SessionStatus.Uncertain, runtime.GetSession("session-b").Status);
    }

    [Fact]
    public void VersionThreeUpgradePreservesExistingWorkWithoutCancellationIntent()
    {
        using var files = new RuntimeFixture();
        SessionSnapshot before;
        BatchSnapshot[] batches;
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a");
            before = runtime.GetSession("session-a");
            batches = runtime.Batches("session-a").ToArray();
        }
        using (var db = new SqliteConnection($"Data Source={files.Database};Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                ALTER TABLE sessions DROP COLUMN cancellation_mode;
                DROP INDEX batch_outstanding;
                CREATE INDEX batch_outstanding ON batches(session_id,point_count,byte_count) WHERE state!='Acknowledged';
                PRAGMA user_version=3;
                """;
            command.ExecuteNonQuery();
        }
        using var upgraded = new DurableRuntime(files.Database);
        Assert.Equal(before, upgraded.GetSession("session-a"));
        Assert.Equal(batches, upgraded.Batches("session-a"));
        Assert.Null(upgraded.GetSession("session-a").Cancellation);
    }

    [Fact]
    public void PendingAttemptEvidencePreventsDiscard()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.Generate("session-a");
        using var db = new SqliteConnection($"Data Source={files.Database};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO attempts(batch_id,state) SELECT id,'Sending' FROM batches";
        command.ExecuteNonQuery();
        var before = runtime.GetSession("session-a");
        Assert.Equal("runtime.cancellation_attempt_exists", Assert.Throws<RuntimeFailure>(() => runtime.Cancel("session-a", CancellationMode.DiscardPending)).Error.Code);
        Assert.Equal(before, runtime.GetSession("session-a"));
        Assert.NotNull(Assert.Single(runtime.Batches("session-a")).Payload);
    }
}
