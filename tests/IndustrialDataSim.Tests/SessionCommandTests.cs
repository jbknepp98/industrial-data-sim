using System.Text;
using System.Text.Json;
using IndustrialDataSim.Cli;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Tests;

public class SessionCommandTests
{
    private static (int Exit, JsonElement Json, string Text) Run(params string[] args)
    {
        using var output = new StringWriter();
        int exit = CliApplication.Run(args, output);
        string text = output.ToString();
        Assert.EndsWith("\n", text);
        Assert.InRange(Encoding.UTF8.GetByteCount(text), 1, 4 * 1024 * 1024);
        return (exit, JsonSerializer.Deserialize<JsonElement>(text), text);
    }

    [Fact]
    public void LifecycleCommandsOperateOnDurableStateAndProduceReadableStatuses()
    {
        using var files = new RuntimeFixture();
        Directory.CreateDirectory(files.Folder);
        string config = Path.Combine(files.Folder, "model.json");
        File.WriteAllText(config, RuntimeFixture.Model().ToJsonString());
        var started = Run("session", "start", files.Database, config);
        Assert.Equal(0, started.Exit);
        Assert.Equal("simulation-only", started.Json.GetProperty("mode").GetString());
        Assert.Contains("No generation worker", started.Text);
        Assert.Equal("Ready", started.Json.GetProperty("result").GetProperty("session").GetProperty("status").GetString());
        foreach (var (action, status) in new[] { ("pause", "Paused"), ("resume", "Ready") })
        {
            var response = Run("session", action, files.Database, "session-a");
            Assert.Equal(0, response.Exit);
            Assert.Equal(status, response.Json.GetProperty("result").GetProperty("session").GetProperty("status").GetString());
        }
        Assert.Equal(0, Run("session", "cancel", files.Database, "session-a", "discard-pending").Exit);
        Assert.Equal(0, Run("session", "release", files.Database, "session-a").Exit);
        var statusResponse = Run("session", "status", files.Database, "session-a");
        Assert.Equal(3, statusResponse.Json.GetProperty("result").GetProperty("progress").GetArrayLength());
        Assert.Equal("Cancelled", statusResponse.Json.GetProperty("result").GetProperty("session").GetProperty("status").GetString());
        Assert.Equal(1, Run("session", "resume", files.Database, "session-a").Exit);
        Assert.True(File.Exists(Path.Combine(files.Folder, "logs", "state.db", "runtime.jsonl")));
    }

    [Theory]
    [InlineData("cancel", "unknown")]
    [InlineData("retry-generation", "127")]
    [InlineData("retry-generation", "4194305")]
    [InlineData("batches", "-1")]
    [InlineData("batches", "not-an-integer")]
    public void InvalidArgumentsNeverCreateState(string action, string option)
    {
        using var files = new RuntimeFixture();
        var response = Run("session", action, files.Database, "session-a", option);
        Assert.Equal(2, response.Exit);
        Assert.Equal("cli.usage", response.Json.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.False(Directory.Exists(files.Folder));
    }

    [Fact]
    public void HelpAndMissingStateDoNotOpenAnEmptyDatabase()
    {
        Assert.Equal(0, Run("session", "help").Exit);
        Assert.Equal(2, Run("session").Exit);
        using var files = new RuntimeFixture();
        var missing = Run("session", "list", files.Database);
        Assert.Equal(1, missing.Exit);
        Assert.Contains("cli.state_missing", missing.Text);
        Assert.DoesNotContain(files.Database, missing.Text);
        Assert.False(Directory.Exists(files.Folder));
    }

    [Fact]
    public void InvalidModelReturnsPreciseValidationBeforeCreatingDatabase()
    {
        using var files = new RuntimeFixture();
        Directory.CreateDirectory(files.Folder);
        string config = Path.Combine(files.Folder, "invalid.json");
        File.WriteAllText(config, "{}");
        var response = Run("session", "start", files.Database, config);
        Assert.Equal(1, response.Exit);
        Assert.NotEqual("$", response.Json.GetProperty("errors")[0].GetProperty("path").GetString());
        Assert.False(File.Exists(files.Database));
        Assert.False(Directory.Exists(Path.Combine(files.Folder, "logs")));
        Assert.Equal(3, Run("session", "start", files.Database, config + ".missing").Exit);
    }

    [Fact]
    public void ListAndBatchMetadataArePaginatedWithoutPayloads()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 1 }))
        {
            for (int i = 0; i < 101; i++) runtime.AddSession(RuntimeFixture.Model($"session-{i:D3}", $"T{i}").ToJsonString());
            var model = RuntimeFixture.Model("session-z", "Z");
            model["session"]!["endUtc"] = "2026-09-01T00:01:00Z";
            model["generators"]![2]!["value"] = "PRIVATE_PAYLOAD_MARKER";
            runtime.AddSession(model.ToJsonString());
            for (int i = 0; i < 101; i++) runtime.Generate("session-z");
        }
        var page = Run("session", "list", files.Database).Json.GetProperty("result");
        Assert.Equal(100, page.GetProperty("sessions").GetArrayLength());
        string cursor = page.GetProperty("nextCursor").GetString()!;
        var next = Run("session", "list", files.Database, cursor).Json.GetProperty("result");
        Assert.Equal(2, next.GetProperty("sessions").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, next.GetProperty("nextCursor").ValueKind);
        var batches = Run("session", "batches", files.Database, "session-z");
        Assert.DoesNotContain("PRIVATE_PAYLOAD_MARKER", batches.Text);
        Assert.DoesNotContain("payload", batches.Text, StringComparison.OrdinalIgnoreCase);
        var batchPage = batches.Json.GetProperty("result");
        Assert.Equal(100, batchPage.GetProperty("batches").GetArrayLength());
        string after = batchPage.GetProperty("nextCursor").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Single(Run("session", "batches", files.Database, "session-z", after).Json.GetProperty("result").GetProperty("batches").EnumerateArray());
    }

    [Fact]
    public void RetryCommandUsesRequestedLimitAndLeavesPayloadUnchanged()
    {
        using var files = new RuntimeFixture();
        BatchSnapshot[] before;
        using (var runtime = new DurableRuntime(files.Database, new() { BatchBytes = 128 }))
        {
            var model = RuntimeFixture.Model();
            model["generators"]![2]!["value"] = new string('<', 100);
            runtime.AddSession(model.ToJsonString());
            runtime.Generate("session-a");
            runtime.Generate("session-a");
            before = runtime.Batches("session-a").ToArray();
        }
        Assert.Equal(1, Run("session", "retry-generation", files.Database, "session-a", "128").Exit);
        var recovered = Run("session", "retry-generation", files.Database, "session-a", "4096");
        Assert.Equal(0, recovered.Exit);
        Assert.Contains("limits are not persisted", recovered.Text);
        using var reopened = new DurableRuntime(files.Database);
        Assert.Equal(before, reopened.Batches("session-a"));
        Assert.Equal(SessionStatus.Ready, reopened.GetSession("session-a").Status);
    }

    [Fact]
    public void DrainCommandRetainsQueueAndUtcStatusExplainsUncertainOutcome()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a");
        }
        Assert.Equal(0, Run("session", "cancel", files.Database, "session-a", "drain").Exit);
        Assert.Equal(1, Run("session", "release", files.Database, "session-a").Exit);
        var waiting = Run("session", "status", files.Database, "session-a").Json.GetProperty("result");
        Assert.Equal("Cancelling", waiting.GetProperty("session").GetProperty("status").GetString());
        Assert.EndsWith("Z", waiting.GetProperty("progress")[0].GetProperty("bufferedUtc").GetString());
        Assert.Equal(JsonValueKind.Null, waiting.GetProperty("progress")[0].GetProperty("acknowledgedUtc").ValueKind);
        using (var runtime = new DurableRuntime(files.Database))
        {
            Assert.All(runtime.Batches("session-a", includePayload: false), batch => Assert.Null(batch.Payload));
            runtime.Finish(runtime.Claim("session-a")!, false);
        }
        var failed = Run("session", "cancel", files.Database, "session-a", "discard-pending");
        Assert.Equal(1, failed.Exit);
        Assert.Contains("runtime.cancellation_locked", failed.Text);
        Assert.Equal("Uncertain", Run("session", "status", files.Database, "session-a").Json.GetProperty("result").GetProperty("session").GetProperty("status").GetString());
    }

    [Fact]
    public void OversizedInspectionReturnsOneBoundedFailureEnvelope()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database)) runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={files.Database};Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE sessions SET error_message=$message";
            command.Parameters.AddWithValue("$message", new string('x', 4 * 1024 * 1024));
            command.ExecuteNonQuery();
        }
        var response = Run("session", "status", files.Database, "session-a");
        Assert.Equal(1, response.Exit);
        Assert.Equal("cli.output_limit", response.Json.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.False(response.Json.GetProperty("valid").GetBoolean());
        Assert.InRange(response.Text.Length, 1, 1000);
    }

    [Fact]
    public void ExistingOwnerReturnsSafeActionableFailure()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        var result = Run("session", "list", files.Database);
        Assert.Equal(1, result.Exit);
        Assert.Contains("runtime.owner_unavailable", result.Text);
        Assert.Contains("Stop the other runtime", result.Text);
        Assert.DoesNotContain(files.Database, result.Text);
    }
}
