using IndustrialDataSim.Runtime;
using Microsoft.Data.Sqlite;

namespace IndustrialDataSim.Tests;

public class StateRestoreTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public async Task RestoreQuarantinesOldWorkAndAllowsOnlyFreshNamespace(int version)
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 1 }))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a");
        }
        if (version < 10) Query(files.Database, "DROP TABLE recovery_sessions;" + (version < 9 ? "DROP TABLE archive_segments;" : "") + $"PRAGMA user_version={version}");
        var backup = StateBackup.Create(files.Database, files.Folder);
        string evidence = Path.Combine(files.Folder, backup.BackupId + ".evidence.sqlite");
        byte[] original = File.ReadAllBytes(evidence);
        var receipt = StateBackup.Restore(evidence, backup.Sha256, version, files.Folder);
        string recoveredPath = Path.Combine(files.Folder, receipt.DatabaseFile);
        using (var recovered = new DurableRuntime(recoveredPath, new() { BatchPoints = 1, SessionQueuePoints = 1, GlobalQueuePoints = 1 }))
        {
            Assert.True(recovered.RecoveryInfo().IsRecovered);
            Assert.Equal(1, recovered.RecoveryInfo().QuarantinedSessions);
            Assert.Equal(SessionStatus.Uncertain, recovered.GetSession("session-a").Status);
            Assert.Equal(BatchStatus.Pending, Assert.Single(recovered.Batches("session-a")).Status);
            foreach (Action mutation in new Action[] {
                () => recovered.Generate("session-a"), () => recovered.Claim("session-a"),
                () => recovered.Pause("session-a"), () => recovered.Resume("session-a"),
                () => recovered.Cancel("session-a", CancellationMode.DiscardPending),
                () => recovered.RetryGeneration("session-a"), () => recovered.ReleaseCompleted("session-a"),
                () => recovered.ArchiveHistory("session-a", files.Folder),
                () => recovered.Archive("session-a", files.Folder) })
                Assert.Equal("backup.session_quarantined", Assert.Throws<RuntimeFailure>(mutation).Error.Code);
            Assert.Equal("backup.fresh_namespace_required", Assert.Throws<RuntimeFailure>(() => recovered.AddSession(RuntimeFixture.Model("new-old-tags").ToJsonString())).Error.Code);
            recovered.AddSession(RuntimeFixture.Model("fresh", receipt.RequiredTagPrefix + "Line").ToJsonString());
            Assert.True(recovered.Generate("fresh").Progressed); // Evidence does not consume active budget.
            var fake = new FakeHistorian();
            Assert.True(await new SimulatedDelivery(recovered, fake).DeliverOneAsync("fresh"));
            Assert.Equal(0, fake.Calls("session-a"));
            Assert.Equal(1, fake.Calls("fresh"));
        }
        using var reopened = new DurableRuntime(recoveredPath);
        Assert.Equal("backup.session_quarantined", Assert.Throws<RuntimeFailure>(() => reopened.Resume("session-a")).Error.Code);
        Assert.Equal(original, File.ReadAllBytes(evidence));
        Assert.Equal("Ready", Query(recoveredPath, "SELECT prior_state FROM recovery_sessions WHERE session_id='session-a'"));
        Assert.Equal(10L, Query(recoveredPath, "PRAGMA user_version"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionRestoreCannotReplayEvenAfterLaterWrites(bool sending)
    {
        using var files = new RuntimeFixture();
        var server = new ProductionDeliveryTests.Server();
        using var client = new HistorianClient(ProductionDeliveryTests.Profile(), new HttpClient(server));
        using (var runtime = new DurableRuntime(files.Database, mode: ExecutionMode.Production))
        {
            var delivery = new ProductionDelivery(runtime, client) { ObservationDelay = TimeSpan.Zero };
            await delivery.AdmitAsync(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a");
            if (sending) runtime.Claim("session-a");
        }
        var backup = StateBackup.Create(files.Database, files.Folder);
        string evidence = Path.Combine(files.Folder, backup.BackupId + ".evidence.sqlite");
        if (!sending)
        {
            using var original = new DurableRuntime(files.Database, mode: ExecutionMode.Production);
            Assert.True(await new ProductionDelivery(original, client) { ObservationDelay = TimeSpan.Zero }.DeliverOneAsync("session-a"));
        }
        var receipt = StateBackup.Restore(evidence, backup.Sha256, 10, files.Folder);
        using var recovered = new DurableRuntime(Path.Combine(files.Folder, receipt.DatabaseFile), mode: ExecutionMode.Production);
        Assert.Equal(sending ? BatchStatus.Sending : BatchStatus.Pending, Assert.Single(recovered.Batches("session-a")).Status);
        Assert.Equal("backup.session_quarantined", recovered.GetSession("session-a").ErrorCode);
        var deliveryAfter = new ProductionDelivery(recovered, client) { ObservationDelay = TimeSpan.Zero };
        Assert.Equal("backup.session_quarantined", (await Assert.ThrowsAsync<RuntimeFailure>(() => deliveryAfter.DeliverOneAsync("session-a"))).Error.Code);
        await deliveryAfter.AdmitAsync(RuntimeFixture.Model("fresh", receipt.RequiredTagPrefix + "Line").ToJsonString());
        recovered.Generate("fresh");
        Assert.True(await deliveryAfter.DeliverOneAsync("fresh"));
        Assert.Equal(sending ? 1 : 2, server.Writes);
    }

    [Fact]
    public void BadDigestAndWrongSchemaCannotCreateRunnableRestore()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database)) runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        var backup = StateBackup.Create(files.Database, files.Folder);
        string evidence = Path.Combine(files.Folder, backup.BackupId + ".evidence.sqlite");
        Assert.Equal("backup.checksum_mismatch", Assert.Throws<RuntimeFailure>(() => StateBackup.Restore(evidence, new string('0', 64), 10, files.Folder)).Error.Code);
        Assert.Equal("backup.restore_schema", Assert.Throws<RuntimeFailure>(() => StateBackup.Restore(evidence, backup.Sha256, 8, files.Folder)).Error.Code);
        Assert.Empty(Directory.GetFiles(files.Folder, "recovered-*.sqlite"));
        foreach (string partial in Directory.GetFiles(files.Folder, "*.partial"))
            Assert.Equal("runtime.incomplete_state", Assert.Throws<RuntimeFailure>(() => new DurableRuntime(partial)).Error.Code);
        var first = StateBackup.Restore(evidence, backup.Sha256, 10, files.Folder);
        var second = StateBackup.Restore(evidence, backup.Sha256, 10, files.Folder);
        Assert.NotEqual(first.DatabaseFile, second.DatabaseFile);
        Assert.NotEqual(first.RequiredTagPrefix, second.RequiredTagPrefix);
    }

    [Fact]
    public void CliRestoreReportsNewFileAndRecoveryNamespace()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database)) runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        var backup = StateBackup.Create(files.Database, files.Folder);
        string evidence = Path.Combine(files.Folder, backup.BackupId + ".evidence.sqlite");
        using var output = new StringWriter();
        Assert.Equal(0, IndustrialDataSim.Cli.CliApplication.Run(["session", "restore", evidence, backup.Sha256, "10", files.Folder], output));
        Assert.Contains("requiredTagPrefix", output.ToString());
        string database = Assert.Single(Directory.GetFiles(files.Folder, "recovered-*.sqlite"));
        using var status = new StringWriter();
        Assert.Equal(0, IndustrialDataSim.Cli.CliApplication.Run(["session", "recovery-info", database], status));
        Assert.Contains("permanently quarantined", status.ToString());
    }

    [Theory]
    [InlineData("DELETE FROM runtime_metadata WHERE key='recovery_namespace'")]
    [InlineData("UPDATE runtime_metadata SET value='PRIVATE_SECRET' WHERE key='recovery_namespace'")]
    public void DamagedRecoveryIdentityCannotEnableOldTagsOrLeakMetadata(string damage)
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database)) runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        var backup = StateBackup.Create(files.Database, files.Folder);
        var receipt = StateBackup.Restore(Path.Combine(files.Folder, backup.BackupId + ".evidence.sqlite"), backup.Sha256, 10, files.Folder);
        string recovered = Path.Combine(files.Folder, receipt.DatabaseFile);
        Query(recovered, damage);
        var failure = Assert.Throws<RuntimeFailure>(() => new DurableRuntime(recovered));
        Assert.Equal("backup.recovery_integrity", failure.Error.Code);
        Assert.DoesNotContain("PRIVATE_SECRET", failure.Message);
        Assert.Equal("backup.session_quarantined", Query(recovered, "SELECT error_code FROM sessions"));
    }

    private static object? Query(string path, string sql)
    {
        using var db = new SqliteConnection($"Data Source={path};Pooling=False");
        db.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
