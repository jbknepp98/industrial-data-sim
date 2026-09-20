using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Tests;

public class ArchiveTests
{
    [Fact]
    public async Task ArchiveRemovesAuditRowsButPreservesIdentityAndTimestampProtection()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        await new SimulationWorker(runtime).RunAsync(100);
        var before = runtime.Progress("session-a");
        var receipt = runtime.Archive("session-a", files.Folder);
        Assert.True(receipt.Batches > 0);
        runtime.VerifyArchive("session-a", files.Folder);
        Assert.Empty(runtime.Batches("session-a"));
        Assert.Empty(runtime.ListSessions());
        Assert.Equal(before, runtime.Progress("session-a"));
        Assert.Equal(receipt, runtime.GetArchive("session-a"));
        Assert.Equal("runtime.session_exists", Assert.Throws<RuntimeFailure>(() => runtime.AddSession(RuntimeFixture.Model().ToJsonString())).Error.Code);
        Assert.Equal("runtime.backward_range", Assert.Throws<RuntimeFailure>(() => runtime.AddSession(RuntimeFixture.Model("replacement").ToJsonString())).Error.Code);
        Assert.Equal("archive.already_archived", Assert.Throws<RuntimeFailure>(() => runtime.Archive("session-a", files.Folder)).Error.Code);
        File.AppendAllText(Path.Combine(files.Folder, receipt.ArchiveId + ".jsonl"), "corrupt");
        Assert.Equal("archive.checksum_mismatch", Assert.Throws<RuntimeFailure>(() => runtime.VerifyArchive("session-a", files.Folder)).Error.Code);
    }

    [Theory]
    [InlineData("after_archive_export", false)]
    [InlineData("before_archive_commit", false)]
    [InlineData("after_archive_commit", true)]
    public async Task InterruptedArchiveHasEitherOriginalRowsOrReceipt(string boundary, bool committed)
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            await new SimulationWorker(runtime).RunAsync(100);
            runtime.FaultPoint = point => { if (point == boundary) throw new SimulatedCrash(); };
            Assert.Throws<SimulatedCrash>(() => runtime.Archive("session-a", files.Folder));
        }
        using var reopened = new DurableRuntime(files.Database);
        Assert.Equal(committed, reopened.Batches("session-a").Count == 0);
        if (committed) reopened.VerifyArchive("session-a", files.Folder);
        else Assert.Equal("archive.missing", Assert.Throws<RuntimeFailure>(() => reopened.GetArchive("session-a")).Error.Code);
    }

    [Fact]
    public async Task DamagedExportCannotAuthorizePruning()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        await new SimulationWorker(runtime).RunAsync(100);
        var batches = runtime.Batches("session-a");
        runtime.FaultPoint = point => {
            if (point == "before_archive_verify")
                File.AppendAllText(Directory.GetFiles(files.Folder, "*.jsonl").Single(), "unexpected");
        };
        Assert.Equal("archive.verification", Assert.Throws<RuntimeFailure>(() => runtime.Archive("session-a", files.Folder)).Error.Code);
        Assert.Equal(batches, runtime.Batches("session-a"));
        Assert.Single(runtime.ListSessions());
    }

    [Fact]
    public void PendingWorkCannotBeArchived()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.Generate("session-a");
        var before = runtime.Batches("session-a");
        var failure = Assert.Throws<RuntimeFailure>(() => runtime.Archive("session-a", files.Folder));
        Assert.Equal("archive.unresolved", failure.Error.Code);
        Assert.Contains("Resolve", failure.Message);
        Assert.Equal(before, runtime.Batches("session-a"));
    }
}
