using System.Security.Cryptography;
using IndustrialDataSim.Cli;
using IndustrialDataSim.Runtime;
using Microsoft.Data.Sqlite;

namespace IndustrialDataSim.Tests;

public class BackupVerificationTests
{
    private static (string Path, StateBackupReceipt Receipt) Snapshot(RuntimeFixture files)
    {
        using (var runtime = new DurableRuntime(files.Database))
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        var receipt = StateBackup.Create(files.Database, files.Folder);
        return (Path.Combine(files.Folder, receipt.BackupId + ".evidence.sqlite"), receipt);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("production")]
    public void CliVerifiesWithoutChangingSnapshotOrCreatingLogs(string command)
    {
        using var files = new RuntimeFixture();
        var (path, receipt) = Snapshot(files);
        byte[] before = File.ReadAllBytes(path);
        var inventory = Directory.GetFiles(files.Folder).Order().ToArray();
        using var output = new StringWriter();
        Assert.Equal(0, CliApplication.Run([command, "verify-backup", path, receipt.Sha256.ToLowerInvariant()], output));
        Assert.Contains("does not authorize", output.ToString());
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(inventory, Directory.GetFiles(files.Folder).Order());
    }

    [Theory]
    [InlineData("corrupt", "backup.checksum_mismatch")]
    [InlineData("digest", "backup.digest_format")]
    [InlineData("missing", "backup.source_missing")]
    [InlineData("wal", "backup.sidecar")]
    [InlineData("journal", "backup.sidecar")]
    public void VerificationFailuresAreActionableAndDoNotEchoInput(string fault, string code)
    {
        using var files = new RuntimeFixture();
        var (path, receipt) = Snapshot(files);
        if (fault == "corrupt") File.AppendAllText(path, "PRIVATE_PAYLOAD");
        if (fault is "wal" or "journal") File.WriteAllText(path + "-" + fault, "PRIVATE_PAYLOAD");
        string inputPath = fault == "missing" ? Path.Combine(files.Folder, "PRIVATE_PATH") : path;
        string digest = fault == "digest" ? "PRIVATE_DIGEST" : receipt.Sha256;
        byte[] before = File.ReadAllBytes(path);
        var failure = Assert.Throws<RuntimeFailure>(() => StateBackup.Verify(inputPath, digest));
        Assert.Equal(code, failure.Error.Code);
        Assert.DoesNotContain("PRIVATE_", failure.Message);
        Assert.True(failure.Message.Length > 80);
        Assert.Equal(before, File.ReadAllBytes(path));
        using var output = new StringWriter();
        Assert.Equal(1, CliApplication.Run(["session", "verify-backup", inputPath, digest], output));
        Assert.Contains(code, output.ToString());
    }

    [Theory]
    [InlineData("PRAGMA user_version=8", "backup.not_evidence")]
    [InlineData("UPDATE runtime_metadata SET value='PRIVATE_MODE' WHERE key='execution_mode'", "backup.execution_mode")]
    [InlineData("INSERT INTO attempts(batch_id,state) VALUES(123456,'Sending')", "backup.integrity")]
    public void MatchingDigestDoesNotBypassStructuralChecks(string damage, string code)
    {
        using var files = new RuntimeFixture();
        var (path, _) = Snapshot(files);
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=OFF; " + damage;
            command.ExecuteNonQuery();
        }
        // Deliberately rehash in this adversarial fixture, not a recovery procedure.
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var failure = Assert.Throws<RuntimeFailure>(() => StateBackup.Verify(path, hash));
        Assert.Equal(code, failure.Error.Code);
        Assert.DoesNotContain("PRIVATE_", failure.Message);
    }
}
