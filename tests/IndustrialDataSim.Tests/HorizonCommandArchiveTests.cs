using System.Text.Json;
using IndustrialDataSim.Cli;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Tests;

public class HorizonCommandArchiveTests
{
    [Fact]
    public async Task ArchiveContainsOriginalAndExtendedHistoryAndVerifiesAfterReopen()
    {
        using var files = new RuntimeFixture();
        ArchiveReceipt receipt;
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            var delivery = new SimulatedDelivery(runtime, new());
            runtime.Generate("session-a"); await delivery.DeliverOneAsync("session-a");
            runtime.ExtendHorizon("session-a", DateTimeOffset.Parse("2026-09-01T00:00:06Z"), 0, Guid.NewGuid().ToString());
            runtime.Generate("session-a"); await delivery.DeliverOneAsync("session-a");
            receipt = runtime.Archive("session-a", files.Folder);
            Assert.Empty(runtime.Batches("session-a"));
        }
        using var reopened = new DurableRuntime(files.Database);
        reopened.VerifyArchive("session-a", files.Folder);
        var records = File.ReadAllLines(Path.Combine(files.Folder, receipt.ArchiveId + ".jsonl"))
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        Assert.Equal(2, records[0].GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, records[0].GetProperty("horizon").GetProperty("revision").GetInt32());
        Assert.Single(records, r => r.GetProperty("kind").GetString() == "horizonRevision");
        Assert.Equal(new[] { 0, 1 }, records.Where(r => r.GetProperty("kind").GetString() == "batch").Select(r => r.GetProperty("horizonRevision").GetInt32()));
        Assert.Equal("extension.archived", Assert.Throws<RuntimeFailure>(() => reopened.ExtendHorizon("session-a", DateTimeOffset.Parse("2026-09-01T00:00:09Z"), 1, Guid.NewGuid().ToString())).Error.Code);
    }

    [Theory]
    [InlineData("session", ExecutionMode.Simulation)]
    [InlineData("production", ExecutionMode.Production)]
    public void CommandsExposeHorizonAndIdempotentExtensionWithoutTransport(string group, ExecutionMode mode)
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database, mode: mode)) runtime.AdmitSession(RuntimeFixture.Model().ToJsonString());
        string request = Guid.NewGuid().ToString();
        for (int retry = 0; retry < 2; retry++)
        {
            using var text = new StringWriter();
            Assert.Equal(0, CliApplication.Run([group, "extend", files.Database, "session-a", "2026-09-01T00:00:06Z", "0", request], text));
            var json = JsonSerializer.Deserialize<JsonElement>(text.ToString());
            Assert.Equal(1, json.GetProperty("result").GetProperty("receipt").GetProperty("revision").GetInt32());
        }
        using var status = new StringWriter();
        Assert.Equal(0, CliApplication.Run([group, "horizon", files.Database, "session-a"], status));
        Assert.Contains("do not repeat automatically", status.ToString());
        using var reopened = new DurableRuntime(files.Database, mode: mode);
        Assert.Empty(reopened.Batches("session-a"));
    }

    [Fact]
    public void InvalidExtensionArgumentsDoNotCreateDatabase()
    {
        using var files = new RuntimeFixture();
        using var text = new StringWriter();
        Assert.Equal(2, CliApplication.Run(["production", "extend", files.Database, "session-a", "PRIVATE_BAD_DATE", "0", Guid.NewGuid().ToString()], text));
        Assert.Contains("cli.extension_arguments", text.ToString());
        Assert.DoesNotContain("PRIVATE_BAD_DATE", text.ToString());
        Assert.False(File.Exists(files.Database));
    }
}
