using System.Security.Cryptography;
using IndustrialDataSim.Runtime;
using Microsoft.Data.Sqlite;

namespace IndustrialDataSim.Tests;

public class StateBackupTests
{
    [Theory]
    [InlineData("session", ExecutionMode.Simulation)]
    [InlineData("production", ExecutionMode.Production)]
    public void CliCreatesSnapshotWithoutCredentials(string command, ExecutionMode mode)
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database, mode: mode)) { }
        using var output = new StringWriter();
        Assert.Equal(0, IndustrialDataSim.Cli.CliApplication.Run(
            [command, "backup", files.Database, files.Folder], output));
        Assert.Contains("Evidence snapshot created", output.ToString());
        Assert.Single(Directory.GetFiles(files.Folder, "*.evidence.sqlite"));
    }

    [Fact]
    public void SnapshotPreservesQueueAndSourceButCannotRun()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a");
        }
        byte[] original = File.ReadAllBytes(files.Database);
        var receipt = StateBackup.Create(files.Database, files.Folder);
        Assert.Equal(10, receipt.SourceSchemaVersion);
        Assert.Equal("simulation-only", receipt.ExecutionMode);
        string backup = Path.Combine(files.Folder, receipt.BackupId + ".evidence.sqlite");
        byte[] snapshot = File.ReadAllBytes(backup);
        Assert.Equal(snapshot.LongLength, receipt.Bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(snapshot)), receipt.Sha256);
        Assert.Equal(original, File.ReadAllBytes(files.Database));
        using (var db = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM batches WHERE state='Pending'";
            Assert.True(Convert.ToInt64(command.ExecuteScalar()) > 0);
        }
        var failure = Assert.Throws<RuntimeFailure>(() => new DurableRuntime(backup));
        Assert.Equal("backup.evidence_only", failure.Error.Code);
        Assert.Contains("Do not", failure.Message);
        Assert.Equal(snapshot, File.ReadAllBytes(backup));
        using var reopened = new DurableRuntime(files.Database);
        Assert.NotEmpty(reopened.Batches("session-a"));
    }

    [Fact]
    public void SnapshotIncludesCommittedWalWithoutMigratingSource()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database)) { }
        // Hold SQLite open to leave committed content in WAL. This is a fixture,
        // not permission for operational writers to bypass the owner lock.
        using var source = new SqliteConnection($"Data Source={files.Database};Pooling=False");
        source.Open();
        using var command = source.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; PRAGMA user_version=7; INSERT INTO runtime_metadata VALUES('backup_test','committed-wal');";
        command.ExecuteNonQuery();
        var receipt = StateBackup.Create(files.Database, files.Folder);
        Assert.Equal(7, receipt.SourceSchemaVersion);
        command.CommandText = "PRAGMA user_version";
        Assert.Equal(7L, command.ExecuteScalar());
        using var snapshot = new SqliteConnection($"Data Source={Path.Combine(files.Folder, receipt.BackupId + ".evidence.sqlite")};Mode=ReadOnly;Pooling=False");
        snapshot.Open();
        using var read = snapshot.CreateCommand();
        read.CommandText = "SELECT value FROM runtime_metadata WHERE key='backup_test'";
        Assert.Equal("committed-wal", read.ExecuteScalar());
    }

    [Fact]
    public void ActiveOwnerPreventsBackupAndExistingFilesAreNotOverwritten()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database))
        {
            Assert.Equal("backup.owner_unavailable", Assert.Throws<RuntimeFailure>(() => StateBackup.Create(files.Database, files.Folder)).Error.Code);
        }
        var first = StateBackup.Create(files.Database, files.Folder);
        var second = StateBackup.Create(files.Database, files.Folder);
        Assert.NotEqual(first.BackupId, second.BackupId);
        Assert.Equal(2, Directory.GetFiles(files.Folder, "*.evidence.sqlite").Length);
    }

    [Fact]
    public void MissingSourceAndInvalidDestinationReturnSafeGuidance()
    {
        using var files = new RuntimeFixture();
        string secretPath = Path.Combine(files.Folder, "private-credential-text");
        var missing = Assert.Throws<RuntimeFailure>(() => StateBackup.Create(secretPath, files.Folder));
        Assert.Equal("backup.source_missing", missing.Error.Code);
        Assert.DoesNotContain("private-credential-text", missing.Message);
        using (var runtime = new DurableRuntime(files.Database)) { }
        File.WriteAllText(secretPath, "private content");
        var invalid = Assert.Throws<RuntimeFailure>(() => StateBackup.Create(files.Database, secretPath));
        Assert.Equal("backup.storage", invalid.Error.Code);
        Assert.Contains("permissions", invalid.Message);
        Assert.DoesNotContain("private", invalid.Message);
    }
}
