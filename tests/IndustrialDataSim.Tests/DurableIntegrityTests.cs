using System.Text.Json.Nodes;
using IndustrialDataSim.Runtime;
using Microsoft.Data.Sqlite;

namespace IndustrialDataSim.Tests;

public class DurableIntegrityTests
{
    [Fact]
    public void FutureDatabaseVersionIsRejectedWithoutResettingIt()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database)) runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        Edit(files.Database, "PRAGMA user_version=99");
        Assert.Equal("runtime.schema_version", Assert.Throws<RuntimeFailure>(() => new DurableRuntime(files.Database)).Error.Code);
        using var db = Open(files.Database);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sessions";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public void FakeRuntimeRefusesDatabaseWithDifferentExecutionMode()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database)) runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        Edit(files.Database, "UPDATE runtime_metadata SET value='production' WHERE key='execution_mode'");
        Assert.Equal("runtime.execution_mode", Assert.Throws<RuntimeFailure>(() => new DurableRuntime(files.Database)).Error.Code);
    }

    [Fact]
    public void ConfigurationHashMismatchCannotAdvanceGeneration()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database)) runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        Edit(files.Database, "UPDATE sessions SET config_hash='changed'");
        using var reopened = new DurableRuntime(files.Database);
        Assert.Equal("runtime.configuration_integrity", Assert.Throws<RuntimeFailure>(() => reopened.Generate("session-a")).Error.Code);
        Assert.Equal(0, reopened.GetSession("session-a").NextSlot);
        Assert.Empty(reopened.Batches("session-a"));
    }

    [Fact]
    public async Task PayloadHashMismatchStopsBeforeCallingTransport()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.GenerateRound();
        }
        Edit(files.Database, "UPDATE batches SET payload='{}'");
        using var reopened = new DurableRuntime(files.Database);
        var fake = new FakeHistorian();
        Assert.False(await new SimulatedDelivery(reopened, fake).DeliverOneAsync("session-a"));
        Assert.Equal(SessionStatus.Failed, reopened.GetSession("session-a").Status);
        Assert.Equal(0, fake.Calls("session-a"));
        Assert.Equal("delivery.payload_integrity", reopened.GetSession("session-a").ErrorCode);
    }

    [Fact]
    public void DatabaseWriteFailureRollsBackPayloadAndCheckpointTogether()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database)) runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        Edit(files.Database, "CREATE TRIGGER reject_checkpoint BEFORE UPDATE OF next_slot ON sessions BEGIN SELECT RAISE(ABORT,'synthetic-private-storage-detail'); END;");
        using var reopened = new DurableRuntime(files.Database);
        var error = Assert.Throws<RuntimeFailure>(() => reopened.Generate("session-a"));
        Assert.Equal("runtime.storage_failure", error.Error.Code);
        Assert.DoesNotContain("synthetic-private-storage-detail", error.Message);
        Assert.Contains("disk space", error.Message);
        Assert.Equal(0, reopened.GetSession("session-a").NextSlot);
        Assert.Empty(reopened.Batches("session-a"));
        Assert.All(reopened.Progress("session-a"), progress => Assert.Null(progress.BufferedTicks));
    }

    [Fact]
    public void AdmissionRollbackDoesNotLeaveOrphanReservations()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.FaultPoint = stage => { if (stage == "before_admission_commit") throw new SimulatedCrash(); };
        Assert.Throws<SimulatedCrash>(() => runtime.AddSession(RuntimeFixture.Model().ToJsonString()));
        Assert.Empty(runtime.Sessions());
        runtime.FaultPoint = null;
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        Assert.Single(runtime.Sessions());
    }

    [Fact]
    public async Task TwoDayRunStreamsBeyondPreviewLimitWithBoundedQueueAndMidrunRestart()
    {
        using var files = new RuntimeFixture();
        var model = RuntimeFixture.Model();
        model["samplingIntervalMs"] = 30000;
        model["session"]!["endUtc"] = "2026-09-03T00:00:00Z";
        var limits = new RuntimeLimits { BatchPoints = 500, SessionQueuePoints = 1000, GlobalQueuePoints = 1000 };
        var fake = new FakeHistorian();
        using (var runtime = new DurableRuntime(files.Database, limits))
        {
            runtime.AddSession(model.ToJsonString());
            runtime.GenerateRound();
            await new SimulatedDelivery(runtime, fake).RunRoundAsync();
            runtime.GenerateRound(); // Leave a Pending batch across restart.
        }
        using var resumed = new DurableRuntime(files.Database, limits);
        var delivery = new SimulatedDelivery(resumed, fake);
        for (int i = 0; i < 100 && resumed.GetSession("session-a").Status != SessionStatus.Complete; i++)
        {
            resumed.GenerateRound();
            Assert.InRange(resumed.GetSession("session-a").QueuedPoints, 0, 1000);
            await delivery.RunRoundAsync();
        }
        Assert.Equal(SessionStatus.Complete, resumed.GetSession("session-a").Status);
        Assert.Equal(17280, resumed.GetSession("session-a").NextSlot);
        string profile = model["session"]!["connectionProfile"]!.GetValue<string>();
        string dataset = model["session"]!["dataset"]!.GetValue<string>();
        for (int i = 0; i < 3; i++)
        {
            var points = fake.Accepted(profile, dataset, "A." + i);
            Assert.Equal(5760, points.Count);
            Assert.Equal(points.Count, points.Select(p => p.Timestamp).Distinct().Count());
            Assert.Equal(points.OrderBy(p => p.Timestamp), points);
            Assert.All(points, p => Assert.Equal(192, p.Quality));
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
    private static void Edit(string path, string sql)
    {
        using var db = Open(path);
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
