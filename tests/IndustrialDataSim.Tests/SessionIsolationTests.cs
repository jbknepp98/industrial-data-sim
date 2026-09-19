using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IndustrialDataSim.Runtime;
using Microsoft.Data.Sqlite;

namespace IndustrialDataSim.Tests;

public class SessionIsolationTests
{
    [Theory]
    [InlineData(false, "hash")]
    [InlineData(false, "version")]
    [InlineData(false, "invalid-model")]
    [InlineData(true, "hash")]
    [InlineData(true, "version")]
    [InlineData(true, "invalid-model")]
    public async Task CorruptSessionStopsWithoutBlockingHealthySession(bool deliveryRound, string damage)
    {
        using var files = new RuntimeFixture();
        using (var initial = new DurableRuntime(files.Database))
        {
            initial.AddSession(RuntimeFixture.Model().ToJsonString());
            initial.AddSession(RuntimeFixture.Model("session-b", "B").ToJsonString());
            if (deliveryRound) initial.GenerateRound();
        }
        Damage(files.Database, damage);
        BatchSnapshot[] badBatches;
        TagProgress[] badProgress;
        var fake = new FakeHistorian();
        string logDirectory = Path.Combine(files.Folder, "logs");
        using var logger = new RuntimeFileLogger(logDirectory);
        using (var runtime = new DurableRuntime(files.Database, logger: logger))
        {
            badBatches = runtime.Batches("session-a").ToArray();
            badProgress = runtime.Progress("session-a").ToArray();
            long cursor = runtime.GetSession("session-a").NextSlot;
            if (deliveryRound)
                Assert.Equal(1, await new SimulatedDelivery(runtime, fake).RunRoundAsync());
            else
            {
                var turns = runtime.GenerateRound();
                Assert.False(turns[0].Progressed);
                Assert.Contains("verified state", turns[0].Reason);
                Assert.True(turns[1].Progressed);
            }
            var failed = runtime.GetSession("session-a");
            Assert.Equal(SessionStatus.Failed, failed.Status);
            Assert.Equal("runtime.configuration_integrity", failed.ErrorCode);
            Assert.Contains("verified state", failed.ErrorMessage);
            Assert.DoesNotContain("PRIVATE_MODEL_DETAIL", failed.ErrorMessage);
            Assert.Equal(cursor, failed.NextSlot);
            Assert.Equal(badBatches, runtime.Batches("session-a"));
            Assert.Equal(badProgress, runtime.Progress("session-a"));
            Assert.Equal(deliveryRound ? SessionStatus.Complete : SessionStatus.Draining, runtime.GetSession("session-b").Status);
            Assert.Equal(0, fake.Calls("session-a"));
            Assert.Throws<RuntimeFailure>(() => runtime.Resume("session-a"));
            Assert.Throws<RuntimeFailure>(() => runtime.RetryGeneration("session-a"));
            Assert.Equal("runtime.tag_owned", Assert.Throws<RuntimeFailure>(() =>
                runtime.AddSession(RuntimeFixture.Model("conflict").ToJsonString())).Error.Code);
        }
        var events = File.ReadAllLines(Path.Combine(logDirectory, "runtime.jsonl"))
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        var failureEvent = Assert.Single(events, entry => entry.GetProperty("eventCode").GetString() == "runtime.configuration_integrity");
        Assert.Equal("session-a", failureEvent.GetProperty("sessionId").GetString());
        Assert.Equal(deliveryRound ? "Claim" : "Generate", failureEvent.GetProperty("operation").GetString());
        Assert.Contains("verified state", failureEvent.GetProperty("action").GetString());
        Assert.DoesNotContain("PRIVATE_MODEL_DETAIL", failureEvent.GetRawText());
        using var reopened = new DurableRuntime(files.Database);
        Assert.Equal(SessionStatus.Failed, reopened.GetSession("session-a").Status);
        Assert.Equal(badBatches, reopened.Batches("session-a"));
        Assert.Equal(badProgress, reopened.Progress("session-a"));
        Assert.Empty(reopened.GenerateRound());
        Assert.Equal(deliveryRound ? 0 : 1, await new SimulatedDelivery(reopened, fake).RunRoundAsync());
        Assert.Equal(0, fake.Calls("session-a"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureToPersistIsolationStopsRoundAsStorageError(bool deliveryRound)
    {
        using var files = new RuntimeFixture();
        using (var initial = new DurableRuntime(files.Database))
        {
            initial.AddSession(RuntimeFixture.Model().ToJsonString());
            initial.AddSession(RuntimeFixture.Model("session-b", "B").ToJsonString());
            if (deliveryRound) initial.GenerateRound();
        }
        Damage(files.Database, "hash");
        Edit(files.Database, """
            CREATE TRIGGER reject_failure BEFORE UPDATE OF state ON sessions
            WHEN NEW.state='Failed' BEGIN SELECT RAISE(ABORT,'PRIVATE_STORAGE_DETAIL'); END;
            """);
        using var runtime = new DurableRuntime(files.Database);
        var before = runtime.GetSession("session-b");
        var fake = new FakeHistorian();
        RuntimeFailure error = deliveryRound
            ? await Assert.ThrowsAsync<RuntimeFailure>(() => new SimulatedDelivery(runtime, fake).RunRoundAsync())
            : Assert.Throws<RuntimeFailure>(() => runtime.GenerateRound());
        Assert.Equal("runtime.storage_failure", error.Error.Code);
        Assert.Contains("disk space", error.Message);
        Assert.DoesNotContain("PRIVATE_STORAGE_DETAIL", error.Message);
        Assert.Equal(before, runtime.GetSession("session-b"));
        Assert.NotEqual(SessionStatus.Failed, runtime.GetSession("session-a").Status);
        Assert.Equal(0, fake.Calls("session-b"));
    }

    [Fact]
    public void UnexpectedProgrammingFailureStillAbortsRound()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.AddSession(RuntimeFixture.Model("session-b", "B").ToJsonString());
        runtime.FaultPoint = stage => { if (stage == "before_generation_commit") throw new InvalidOperationException("synthetic failure"); };
        Assert.Throws<InvalidOperationException>(() => runtime.GenerateRound());
        Assert.Equal(0, runtime.GetSession("session-a").NextSlot);
        Assert.Equal(0, runtime.GetSession("session-b").NextSlot);
    }

    private static void Damage(string database, string damage)
    {
        if (damage == "invalid-model")
        {
            const string json = "{\"PRIVATE_MODEL_DETAIL\":true}";
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            Edit(database, $"UPDATE sessions SET config='{json}',config_hash='{hash}' WHERE id='session-a'");
        }
        else Edit(database, damage == "hash"
            ? "UPDATE sessions SET config_hash='changed' WHERE id='session-a'"
            : "UPDATE sessions SET generator_version=99 WHERE id='session-a'");
    }

    private static void Edit(string database, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
