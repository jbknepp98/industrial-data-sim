using System.Text.Json.Nodes;
using IndustrialDataSim.Runtime;
using Microsoft.Data.Sqlite;

namespace IndustrialDataSim.Tests;

public class SessionProgressTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgressSurvivesReleaseReuseAndReopen(bool migrateOldDatabase)
    {
        using var files = new RuntimeFixture();
        TagProgress[] original, buffered;
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a");
            await new SimulatedDelivery(runtime, new()).DeliverOneAsync("session-a");
            original = runtime.Progress("session-a").ToArray();
            runtime.ReleaseCompleted("session-a");
            Assert.Equal(original, runtime.Progress("session-a"));
            Assert.Equal("runtime.backward_range", Assert.Throws<RuntimeFailure>(() =>
                runtime.AddSession(RuntimeFixture.Model("backward").ToJsonString())).Error.Code);
            var later = RuntimeFixture.Model("session-b", "a"); // Same keys, different display case.
            later["session"]!["startUtc"] = "2026-09-02T00:00:00Z";
            later["session"]!["endUtc"] = "2026-09-02T00:00:03Z";
            runtime.AddSession(later.ToJsonString());
            Assert.All(runtime.Progress("session-b"), entry =>
            {
                Assert.Null(entry.BufferedTicks);
                Assert.Null(entry.SubmittedTicks);
                Assert.Null(entry.AcknowledgedTicks);
            });
            runtime.Generate("session-b");
            buffered = runtime.Progress("session-b").ToArray();
            Assert.All(buffered, entry => { Assert.NotNull(entry.BufferedTicks); Assert.Null(entry.SubmittedTicks); });
            Assert.Equal(original, runtime.Progress("session-a"));
        }
        if (migrateOldDatabase) Downgrade(files.Database);
        using (var runtime = new DurableRuntime(files.Database))
        {
            Assert.Equal(original, runtime.Progress("session-a"));
            Assert.Equal(buffered, runtime.Progress("session-b"));
            Assert.True(await new SimulatedDelivery(runtime, new()).DeliverOneAsync("session-b"));
            runtime.ReleaseCompleted("session-b");
            runtime.ReleaseCompleted("session-a"); // Repeated release cannot erase either report.
            Assert.Equal(original, runtime.Progress("session-a"));
            Assert.All(runtime.Progress("session-b"), entry => Assert.Equal(entry.BufferedTicks, entry.AcknowledgedTicks));
        }
        using var reopened = new DurableRuntime(files.Database);
        Assert.Equal(original, reopened.Progress("session-a"));
        Assert.All(reopened.Progress("session-b"), entry => Assert.True(entry.AcknowledgedTicks > original[0].AcknowledgedTicks));
    }

    [Fact]
    public async Task MigrationRetainsTagsThatNeverEmitted()
    {
        using var files = new RuntimeFixture();
        var model = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "boolean-gate-simulation.json")))!.AsObject();
        model["generators"]![0] = JsonNode.Parse("""{"tag":"Example.Ready.Boolean","kind":"constant","value":false}""");
        string id = model["session"]!["sessionId"]!.GetValue<string>();
        TagProgress[] before;
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.AddSession(model.ToJsonString());
            runtime.Generate(id);
            await new SimulatedDelivery(runtime, new()).DeliverOneAsync(id);
            before = runtime.Progress(id).ToArray();
            Assert.Equal(2, before.Count(entry => entry.BufferedTicks is null));
            runtime.ReleaseCompleted(id);
        }
        Downgrade(files.Database);
        using var reopened = new DurableRuntime(files.Database);
        Assert.Equal(before, reopened.Progress(id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MigrationSeparatesSubmissionFromAcknowledgement(bool sending)
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a");
            if (sending) Assert.NotNull(runtime.Claim("session-a"));
        }
        Downgrade(files.Database);
        using var reopened = new DurableRuntime(files.Database);
        Assert.All(reopened.Progress("session-a"), entry =>
        {
            Assert.NotNull(entry.BufferedTicks);
            Assert.Equal(sending ? entry.BufferedTicks : null, entry.SubmittedTicks);
            Assert.Null(entry.AcknowledgedTicks);
        });
        Assert.Equal(sending ? SessionStatus.Uncertain : SessionStatus.Draining, reopened.GetSession("session-a").Status);
    }

    [Theory]
    [InlineData("UPDATE batches SET positions='PRIVATE_BROKEN_METADATA'", "runtime.progress_migration")]
    [InlineData("UPDATE sessions SET config_hash='changed'", "runtime.configuration_integrity")]
    [InlineData("UPDATE batches SET positions='{}'", "runtime.progress_migration")]
    [InlineData("UPDATE batches SET positions='{\"A.0\":0,\"A.0\":1}'", "runtime.progress_migration")]
    public void UnreconstructableHistoryRollsBackWholeMigration(string damage, string expectedCode)
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a");
        }
        Downgrade(files.Database);
        Edit(files.Database, damage);
        var error = Assert.Throws<RuntimeFailure>(() => new DurableRuntime(files.Database));
        Assert.Equal(expectedCode, error.Error.Code);
        Assert.DoesNotContain("PRIVATE_BROKEN_METADATA", error.Message);
        using var db = Open(files.Database);
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        Assert.Equal(2L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='session_tag_progress'";
        Assert.Equal(0L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM batches";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    private static void Downgrade(string database) => Edit(database, "ALTER TABLE sessions DROP COLUMN cancellation_mode; DROP TABLE session_tag_progress; PRAGMA user_version=2;");
    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        return db;
    }
    private static void Edit(string database, string sql)
    {
        using var db = Open(database);
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
