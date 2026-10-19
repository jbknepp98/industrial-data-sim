using System.Text.Json;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Tests;

public class ArchiveHistoryTests
{
    [Fact]
    public void RollingHistoryPreservesTheExactProcessStreamAndOwnership()
    {
        using var baselineFiles = new RuntimeFixture();
        using var rolledFiles = new RuntimeFixture();
        var model = WindowedManufacturingTests.Model(60);
        model["manufacturing"]!["execution"] = "windowed";
        string json = model.ToJsonString();
        var limits = new RuntimeLimits { BatchPoints = 7 };
        List<string> Run(RuntimeFixture files, bool rollover)
        {
            using var runtime = new DurableRuntime(files.Database, limits);
            runtime.AddSession(json);
            var payloads = new List<string>();
            while (runtime.GetSession("session-a").Status != SessionStatus.Complete)
            {
                runtime.Generate("session-a");
                if (runtime.Claim("session-a") is { } work)
                {
                    payloads.Add(work.Batch.Payload!);
                    runtime.Finish(work, true);
                }
                if (!rollover || payloads.Count % 10 != 0 || runtime.GetSession("session-a").Status == SessionStatus.Complete) continue;
                runtime.Pause("session-a");
                var before = runtime.GetSession("session-a");
                var progress = runtime.Progress("session-a");
                var receipt = runtime.ArchiveHistory("session-a", files.Folder, 10);
                runtime.VerifyArchiveSegment("session-a", receipt.ArchiveId, files.Folder);
                Assert.Equal(10, receipt.Batches);
                Assert.Equal(before, runtime.GetSession("session-a"));
                Assert.Equal(progress, runtime.Progress("session-a"));
                var conflict = System.Text.Json.Nodes.JsonNode.Parse(json)!;
                conflict["session"]!["sessionId"] = "conflict";
                Assert.Equal("runtime.tag_owned", Assert.Throws<RuntimeFailure>(() => runtime.AddSession(conflict.ToJsonString())).Error.Code);
                runtime.Resume("session-a");
            }
            if (rollover)
            {
                Assert.NotEmpty(runtime.ArchiveSegments("session-a"));
                var receipt = runtime.Archive("session-a", files.Folder);
                runtime.VerifyArchive("session-a", files.Folder);
                Assert.Contains(File.ReadLines(Path.Combine(files.Folder, receipt.ArchiveId + ".jsonl")), line => line.Contains("priorSegment"));
            }
            return payloads;
        }
        Assert.Equal(Run(baselineFiles, false), Run(rolledFiles, true));
    }

    [Fact]
    public async Task ProductionSegmentRetainsReviewEvidenceAndNeverChangesPublishProgress()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 3 }, mode: ExecutionMode.Production);
        var server = new ProductionDeliveryTests.Server();
        using var client = new HistorianClient(ProductionDeliveryTests.Profile(), new HttpClient(server));
        var delivery = new ProductionDelivery(runtime, client) { ObservationDelay = TimeSpan.Zero };
        await delivery.AdmitAsync(RuntimeFixture.Model().ToJsonString());
        runtime.Generate("session-a");
        Assert.True(await delivery.DeliverOneAsync("session-a"));
        runtime.RecordUserReview("session-a", true);
        runtime.Generate("session-a");
        runtime.Pause("session-a");
        var pending = Assert.Single(runtime.Batches("session-a"), b => b.Status == BatchStatus.Pending);
        var progress = runtime.Progress("session-a");
        var receipt = runtime.ArchiveHistory("session-a", files.Folder, 1);
        runtime.VerifyArchiveSegment("session-a", receipt.ArchiveId, files.Folder);
        Assert.Equal(progress, runtime.Progress("session-a"));
        Assert.Equal(pending, Assert.Single(runtime.Batches("session-a")));
        string exported = File.ReadAllText(Path.Combine(files.Folder, receipt.ArchiveId + ".jsonl"));
        Assert.Contains("Accepted", exported);
        Assert.Contains("Published", exported);
        Assert.Equal(1, server.Writes);
        runtime.Resume("session-a");
        Assert.True(await delivery.DeliverOneAsync("session-a"));
        Assert.Equal(2, server.Writes);
    }

    [Fact]
    public void DiskPressurePreservesWindowedCheckpointBeforeResuming()
    {
        using var files = new RuntimeFixture();
        var model = WindowedManufacturingTests.Model();
        model["manufacturing"]!["execution"] = "windowed";
        using var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 7 });
        runtime.AddSession(model.ToJsonString());
        runtime.Generate("session-a");
        var progress = runtime.Progress("session-a");
        var batches = runtime.Batches("session-a");
        var session = runtime.GetSession("session-a");
        runtime.FreeDiskBytes = () => 0;
        Assert.False(runtime.Generate("session-a").Progressed);
        Assert.Equal(progress, runtime.Progress("session-a"));
        Assert.Equal(batches, runtime.Batches("session-a"));
        Assert.Equal(session, runtime.GetSession("session-a"));
        runtime.FreeDiskBytes = () => long.MaxValue;
        Assert.True(runtime.Generate("session-a").Progressed);
        Assert.Equal(14, runtime.GetSession("session-a").NextSlot);
    }

    [Theory]
    [InlineData("after_archive_export", false)]
    [InlineData("before_archive_commit", false)]
    [InlineData("after_archive_commit", true)]
    public void InterruptedSegmentEitherKeepsRowsOrRetainsVerifiedReceipt(string boundary, bool committed)
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 1 }))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a"); runtime.Finish(runtime.Claim("session-a")!, true);
            runtime.Generate("session-a"); // Pending must survive either outcome.
            runtime.Pause("session-a");
            runtime.FaultPoint = at => { if (at == boundary) throw new SimulatedCrash(); };
            Assert.Throws<SimulatedCrash>(() => runtime.ArchiveHistory("session-a", files.Folder, 1));
        }
        using var reopened = new DurableRuntime(files.Database);
        Assert.Equal(committed ? 1 : 2, reopened.Batches("session-a").Count);
        Assert.Single(reopened.Batches("session-a"), b => b.Status == BatchStatus.Pending && b.Payload is not null);
        Assert.Equal(committed ? 1 : 0, reopened.ArchiveSegments("session-a").Count);
        if (committed) reopened.VerifyArchiveSegment("session-a", reopened.ArchiveSegments("session-a")[0].ArchiveId, files.Folder);
        Assert.Equal(SessionStatus.Paused, reopened.GetSession("session-a").Status);
    }

    [Fact]
    public void LowDiskAndDamagedExportCannotPruneHistory()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.Generate("session-a"); runtime.Finish(runtime.Claim("session-a")!, true);
        runtime.FreeDiskBytes = () => 0;
        var before = runtime.Batches("session-a");
        Assert.Equal("archive.disk_low", Assert.Throws<RuntimeFailure>(() => runtime.ArchiveHistory("session-a", files.Folder)).Error.Code);
        Assert.Equal(before, runtime.Batches("session-a"));
        runtime.FreeDiskBytes = () => long.MaxValue;
        runtime.FaultPoint = at => {
            if (at == "before_archive_verify") File.AppendAllText(Directory.GetFiles(files.Folder, "*.jsonl").Single(), "damaged");
        };
        Assert.Equal("archive.verification", Assert.Throws<RuntimeFailure>(() => runtime.ArchiveHistory("session-a", files.Folder)).Error.Code);
        Assert.Equal(before, runtime.Batches("session-a"));
        Assert.Empty(runtime.ArchiveSegments("session-a"));
    }

    [Fact]
    public void RunningAndUncertainSessionsCannotBePruned()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 1 });
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.Generate("session-a");
        Assert.Equal("archive.history_state", Assert.Throws<RuntimeFailure>(() => runtime.ArchiveHistory("session-a", files.Folder)).Error.Code);
        runtime.Finish(runtime.Claim("session-a")!, false);
        var before = runtime.Batches("session-a");
        Assert.Equal("archive.history_state", Assert.Throws<RuntimeFailure>(() => runtime.ArchiveHistory("session-a", files.Folder)).Error.Code);
        Assert.Equal(before, runtime.Batches("session-a"));
    }

    [Fact]
    public void CliHistoryAndVerificationUseBoundedReceipts()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a"); runtime.Finish(runtime.Claim("session-a")!, true);
        }
        using var output = new StringWriter();
        Assert.Equal(0, IndustrialDataSim.Cli.CliApplication.Run(["session", "archive-history", files.Database, "session-a", files.Folder, "1"], output));
        using var runtimeAfter = new DurableRuntime(files.Database);
        var receipt = Assert.Single(runtimeAfter.ArchiveSegments("session-a"));
        runtimeAfter.VerifyArchiveSegment("session-a", receipt.ArchiveId, files.Folder);
        Assert.Empty(runtimeAfter.ArchiveSegments("session-a", receipt.ThroughBatch));
        File.AppendAllText(Path.Combine(files.Folder, receipt.ArchiveId + ".jsonl"), "damaged");
        Assert.Equal("archive.checksum_mismatch", Assert.Throws<RuntimeFailure>(() => runtimeAfter.VerifyArchiveSegment("session-a", receipt.ArchiveId, files.Folder)).Error.Code);
        Assert.Equal("archive.segment_id", Assert.Throws<RuntimeFailure>(() => runtimeAfter.VerifyArchiveSegment("session-a", "../PRIVATE", files.Folder)).Error.Code);
    }
}
