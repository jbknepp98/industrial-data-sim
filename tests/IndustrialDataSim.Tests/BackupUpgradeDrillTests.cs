using IndustrialDataSim.Runtime;
using Microsoft.Data.Sqlite;

namespace IndustrialDataSim.Tests;

public class BackupUpgradeDrillTests
{
    [Fact]
    public void LegacyUpgradePreservesQueuedWorkAndSnapshotRemainsBlocked()
    {
        using var files = new RuntimeFixture();
        BatchSnapshot[] batches;
        SessionSnapshot session;
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a");
            batches = runtime.Batches("session-a").ToArray();
            session = runtime.GetSession("session-a");
        }
        Query(files.Database, "DROP TABLE archive_segments; DROP TABLE process_checkpoints; PRAGMA user_version=7");
        var receipt = StateBackup.Create(files.Database, files.Folder);
        string path = Path.Combine(files.Folder, receipt.BackupId + ".evidence.sqlite");
        Assert.Equal(7, receipt.SourceSchemaVersion);
        StateBackup.Verify(path, receipt.Sha256);
        using (var upgraded = new DurableRuntime(files.Database))
        {
            Assert.Equal(session, upgraded.GetSession("session-a"));
            Assert.Equal(batches, upgraded.Batches("session-a"));
        }
        Assert.Equal(9L, Query(files.Database, "PRAGMA user_version"));
        Assert.Equal("backup.evidence_only", Assert.Throws<RuntimeFailure>(() => new DurableRuntime(path)).Error.Code);
        StateBackup.Verify(path, receipt.Sha256);
    }

    [Fact]
    public void FailedUpgradePreservesVersionAndQueueForInvestigation()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a");
        }
        // Simulate a conflicting table in a damaged legacy database. Migration
        // must fail rather than drop the table or invent a process checkpoint.
        Query(files.Database, "PRAGMA user_version=7");
        var receipt = StateBackup.Create(files.Database, files.Folder);
        object? pending = Query(files.Database, "SELECT payload FROM batches LIMIT 1");
        Assert.Equal("runtime.storage_failure", Assert.Throws<RuntimeFailure>(() => new DurableRuntime(files.Database)).Error.Code);
        Assert.Equal(7L, Query(files.Database, "PRAGMA user_version"));
        Assert.Equal(pending, Query(files.Database, "SELECT payload FROM batches LIMIT 1"));
        StateBackup.Verify(Path.Combine(files.Folder, receipt.BackupId + ".evidence.sqlite"), receipt.Sha256);
    }

    [Fact]
    public void WindowedCheckpointAndQueueArePreservedInVerifiedEvidence()
    {
        using var files = new RuntimeFixture();
        var model = WindowedManufacturingTests.Model();
        model["manufacturing"]!["execution"] = "windowed";
        using (var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 7 }))
        {
            runtime.AddSession(model.ToJsonString());
            runtime.Generate("session-a");
        }
        var receipt = StateBackup.Create(files.Database, files.Folder);
        string path = Path.Combine(files.Folder, receipt.BackupId + ".evidence.sqlite");
        StateBackup.Verify(path, receipt.Sha256);
        foreach (string query in new[] {
            "SELECT payload FROM process_checkpoints", "SELECT hash FROM process_checkpoints",
            "SELECT next_slot FROM process_checkpoints", "SELECT next_slot FROM sessions",
            "SELECT payload FROM batches LIMIT 1", "SELECT hash FROM batches LIMIT 1" })
            Assert.Equal(Query(files.Database, query), Query(path, query));
        using var resumed = new DurableRuntime(files.Database, new() { BatchPoints = 7 });
        Assert.True(resumed.Generate("session-a").Progressed);
        Assert.Equal(14, resumed.GetSession("session-a").NextSlot);
        Assert.Equal(7L, Query(path, "SELECT next_slot FROM sessions"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleProductionSnapshotCannotReplayLaterPublish(bool interrupted)
    {
        using var files = new RuntimeFixture();
        var server = new ProductionDeliveryTests.Server();
        using var client = new HistorianClient(ProductionDeliveryTests.Profile(), new HttpClient(server));
        using (var runtime = new DurableRuntime(files.Database, mode: ExecutionMode.Production))
        {
            var delivery = new ProductionDelivery(runtime, client) { ObservationDelay = TimeSpan.Zero };
            await delivery.AdmitAsync(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a");
        }
        var receipt = StateBackup.Create(files.Database, files.Folder);
        string path = Path.Combine(files.Folder, receipt.BackupId + ".evidence.sqlite");
        using (var runtime = new DurableRuntime(files.Database, mode: ExecutionMode.Production))
        {
            if (interrupted)
            {
                // Durable intent alone is sufficient for uncertainty; no network
                // outcome may be inferred after the original state is lost.
                Assert.NotNull(runtime.Claim("session-a"));
            }
            else
            {
                var delivery = new ProductionDelivery(runtime, client) { ObservationDelay = TimeSpan.Zero };
                Assert.True(await delivery.DeliverOneAsync("session-a"));
                Assert.Equal(1, server.Writes);
            }
        }
        using (var original = new DurableRuntime(files.Database, mode: ExecutionMode.Production))
            Assert.Equal(interrupted ? BatchStatus.Uncertain : BatchStatus.Published,
                Assert.Single(original.Batches("session-a")).Status);
        Assert.Equal("Pending", Query(path, "SELECT state FROM batches LIMIT 1"));
        Assert.Equal("production", StateBackup.Verify(path, receipt.Sha256).ExecutionMode);
        Assert.Equal("backup.evidence_only", Assert.Throws<RuntimeFailure>(() =>
            new DurableRuntime(path, mode: ExecutionMode.Production)).Error.Code);
        Assert.Equal(interrupted ? 0 : 1, server.Writes);
    }

    private static object? Query(string path, string sql)
    {
        using var db = new SqliteConnection($"Data Source={path};Pooling=False");
        db.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
