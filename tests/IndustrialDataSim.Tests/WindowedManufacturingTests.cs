using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Simulation;
using IndustrialDataSim.Runtime;
using Microsoft.Data.Sqlite;

namespace IndustrialDataSim.Tests;

public class WindowedManufacturingTests
{
    internal static JsonObject Model(int seconds = 60)
    {
        // Existing fixture combines previous-tick feedback, held timers, noise,
        // faults and both accumulation modes in a small independently readable model.
        var model = ManufacturingTests.Model("""
            [{"tag":"Ready","expression":{"op":"schedule","repeat":true,"steps":[{"durationMs":2000,"value":false},{"durationMs":4000,"value":true}]}},
             {"tag":"State","state":{"initial":"Idle","transitions":[{"from":"*","to":"Idle","when":{"op":"not","arg":{"tag":"Ready"}}},{"from":"Idle","to":"Running","when":{"op":"held","arg":{"tag":"Ready"},"durationMs":1000}}]}},
             {"tag":"Feedback","expression":{"op":"add","left":{"previous":"Feedback","initial":0},"right":1},"noise":{"amplitude":0.1,"seed":3},"faults":[{"kind":"freeze","startMs":4000,"durationMs":5000},{"kind":"quality","startMs":5000,"durationMs":2000,"quality":0}]},
             {"tag":"TimeTotal","accumulator":{"mode":"time","initial":0,"enabled":{"tag":"Ready"},"ratePerSecond":10}},
             {"tag":"BatchTotal","accumulator":{"mode":"batch","initial":0,"enabled":{"tag":"Ready"},"quantityRange":{"minimum":10,"maximum":20},"durationRangeMs":{"minimum":2000,"maximum":4000},"seed":17}}]
            """, seconds);
        model["session"]!["outputTags"]![0]!["valueType"] = "boolean";
        model["session"]!["outputTags"]![1]!["valueType"] = "string";
        return model;
    }

    [Theory]
    [InlineData(500, 1)]
    [InlineData(1000, 7)]
    [InlineData(2000, 13)]
    [InlineData(3000, 1000)]
    public void CheckpointWindowsMatchReferenceIncludingPartialRows(int sampleMs, int batchPoints)
    {
        var model = Model(); model["samplingIntervalMs"] = sampleMs;
        var reference = DryRun.Generate(Load(model)).Data!;
        model["manufacturing"]!["execution"] = "windowed";
        long cursor = 0;
        string? checkpoint = null;
        var actual = reference.Keys.ToDictionary(tag => tag, _ => new List<string>());
        while (true)
        {
            var definition = Load(model); // Simulates losing every in-memory object.
            if (checkpoint is not null) definition.RestoreProcessCheckpoint(checkpoint, cursor);
            if (cursor == GenerationWindow.TotalSlots(definition)) break;
            var window = GenerationWindow.Generate(definition, cursor, 10000, batchPoints, 100000);
            Assert.Null(window.Error); Assert.True(window.NextSlot > cursor);
            using var payload = JsonDocument.Parse(window.Payload);
            foreach (var tag in payload.RootElement.EnumerateObject())
                actual[tag.Name].AddRange(tag.Value.EnumerateArray().Select(point => point.GetRawText()));
            cursor = window.NextSlot; checkpoint = definition.CaptureProcessCheckpoint();
        }
        foreach (var tag in reference)
            Assert.Equal(tag.Value.Select(point => JsonSerializer.Serialize(point)), actual[tag.Key]);
    }

    [Theory]
    [InlineData("before_generation_commit", 0)]
    [InlineData("after_generation_commit", 7)]
    public void GenerationCommitIncludesProcessState(string fault, int expectedCursor)
    {
        using var files = new RuntimeFixture();
        var model = Model(); model["manufacturing"]!["execution"] = "windowed";
        var limits = new RuntimeLimits { BatchPoints = 7 };
        using (var runtime = new DurableRuntime(files.Database, limits))
        {
            runtime.AddSession(model.ToJsonString());
            runtime.FaultPoint = at => { if (at == fault) throw new InvalidOperationException("injected"); };
            Assert.Throws<InvalidOperationException>(() => runtime.Generate("session-a"));
        }
        using var resumed = new DurableRuntime(files.Database, limits);
        Assert.Equal(expectedCursor, resumed.GetSession("session-a").NextSlot);
        Assert.True(resumed.Generate("session-a").Progressed);
        Assert.Equal(expectedCursor + 7, resumed.GetSession("session-a").NextSlot);
    }

    [Fact]
    public void DamagedProcessCheckpointFailsOnlyItsSession()
    {
        using var files = new RuntimeFixture();
        var model = Model(); model["manufacturing"]!["execution"] = "windowed";
        using var runtime = new DurableRuntime(files.Database, new RuntimeLimits { BatchPoints = 7 });
        runtime.AddSession(model.ToJsonString()); runtime.Generate("session-a");
        runtime.AddSession(RuntimeFixture.Model("healthy", "Other").ToJsonString());
        using (var connection = new SqliteConnection($"Data Source={files.Database};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE process_checkpoints SET hash='bad'"; command.ExecuteNonQuery();
        }
        runtime.GenerateRound();
        Assert.Equal("manufacturing.checkpoint_integrity", runtime.GetSession("session-a").ErrorCode);
        Assert.Equal(7, runtime.GetSession("session-a").NextSlot);
        Assert.True(runtime.GetSession("healthy").NextSlot > 0);
    }

    [Fact]
    public void SeventyTwoHoursAdmitsWithoutMaterializingTheHorizon()
    {
        var model = Model(72 * 3600); model["manufacturing"]!["execution"] = "windowed";
        var definition = Load(model);
        Assert.Equal(72 * 3600 * 5, GenerationWindow.TotalSlots(definition));
        Assert.True(definition.CaptureProcessCheckpoint()!.Length < 10000);
        var window = GenerationWindow.Generate(definition, 0, 100000, 100000, 4000000);
        Assert.Null(window.Error);
        Assert.InRange(window.NextSlot, 1, 5010); // Work is bounded independently of caller limits.
    }

    [Fact]
    public void LaterArithmeticFailurePreservesWindowCursorAndRedactsValues()
    {
        var model = ManufacturingTests.Model("""[{"tag":"A","expression":{"op":"divide","left":1,"right":{"op":"subtract","left":1000,"right":{"op":"elapsedMs"}}}}]""");
        model["manufacturing"]!["execution"] = "windowed";
        var definition = Load(model);
        var window = GenerationWindow.Generate(definition, 0, 100, 100, 100000);
        Assert.Equal(0, window.NextSlot); Assert.Equal("{}", window.Payload);
        Assert.Equal("manufacturing.division_by_zero", window.Error!.Code);
        Assert.EndsWith(".right", window.Error.Path);
    }

    [Fact]
    public async Task CompletedWindowedSessionExtendsAndArchivesItsCheckpoint()
    {
        using var files = new RuntimeFixture();
        var model = Model(3); model["manufacturing"]!["execution"] = "windowed";
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(model.ToJsonString());
        var worker = new SimulationWorker(runtime);
        await worker.RunAsync(100);
        Assert.Equal(SessionStatus.Complete, runtime.GetSession("session-a").Status);
        runtime.ExtendHorizon("session-a", DateTimeOffset.Parse("2026-09-01T00:00:06Z"), 0, Guid.NewGuid().ToString());
        await worker.RunAsync(100);
        Assert.Equal(30, runtime.GetSession("session-a").NextSlot);
        var receipt = runtime.Archive("session-a", files.Folder);
        string export = File.ReadAllText(Path.Combine(files.Folder, receipt.ArchiveId + ".jsonl"));
        Assert.Contains("processCheckpoint", export);
        Assert.Contains("horizonRevision", export);
    }

    [Fact]
    public async Task ProductionWindowedPayloadUsesNormalBlindPublishPath()
    {
        using var files = new RuntimeFixture();
        var model = Model(3); model["manufacturing"]!["execution"] = "windowed";
        using var runtime = new DurableRuntime(files.Database, mode: ExecutionMode.Production);
        var server = new ProductionDeliveryTests.Server();
        using var client = new HistorianClient(ProductionDeliveryTests.Profile(), new HttpClient(server));
        var delivery = new ProductionDelivery(runtime, client) { ObservationDelay = TimeSpan.Zero };
        await delivery.AdmitAsync(model.ToJsonString());
        var result = await new ProductionWorker(runtime, delivery).RunAsync(100);
        Assert.Equal(SessionStatus.Complete, runtime.GetSession("session-a").Status);
        Assert.Equal(1, result.PublishedBatches);
        Assert.Equal(1, server.Writes);
    }

    [Fact]
    public void FailedOversizeWindowDoesNotCommitAnAdvancedProcessClock()
    {
        using var files = new RuntimeFixture();
        var model = ManufacturingTests.Model("[{\"tag\":\"A\",\"expression\":\"" + new string('x', 500) + "\"}]");
        model["session"]!["outputTags"]![0]!["valueType"] = "string";
        model["manufacturing"]!["execution"] = "windowed";
        using (var runtime = new DurableRuntime(files.Database, new() { BatchBytes = 128 }))
        {
            runtime.AddSession(model.ToJsonString()); runtime.Generate("session-a");
            Assert.Equal("generation.point_too_large", runtime.GetSession("session-a").ErrorCode);
            Assert.Equal(0, runtime.GetSession("session-a").NextSlot);
        }
        using var resumed = new DurableRuntime(files.Database);
        resumed.RetryGeneration("session-a");
        Assert.True(resumed.Generate("session-a").Progressed);
    }

    [Fact]
    public void WallClockFenceDoesNotAdvancePastTheCurrentSample()
    {
        var model = Model(); model["manufacturing"]!["execution"] = "windowed";
        var definition = Load(model);
        var first = GenerationWindow.Generate(definition, 0, 1000, 1000, 100000, definition.Session.StartUtc);
        Assert.Equal(5, first.NextSlot);
        string checkpoint = definition.CaptureProcessCheckpoint()!;
        var next = Load(model); next.RestoreProcessCheckpoint(checkpoint, first.NextSlot);
        var blocked = GenerationWindow.Generate(next, first.NextSlot, 1000, 1000, 100000, definition.Session.StartUtc);
        Assert.Equal(first.NextSlot, blocked.NextSlot);
        Assert.Equal(checkpoint, next.CaptureProcessCheckpoint());
    }

    [Fact]
    public async Task ResidentProductionTurnsYieldFairlyBetweenSessions()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, mode: ExecutionMode.Production);
        var server = new ProductionDeliveryTests.Server();
        using var client = new HistorianClient(ProductionDeliveryTests.Profile(), new HttpClient(server));
        var delivery = new ProductionDelivery(runtime, client) { ObservationDelay = TimeSpan.Zero };
        await delivery.AdmitAsync(RuntimeFixture.Model("session-a", "A").ToJsonString());
        await delivery.AdmitAsync(RuntimeFixture.Model("session-b", "B").ToJsonString());
        var worker = new ProductionWorker(runtime, delivery);
        var first = await worker.RunAsync(1, maximumSessionTurns: 1);
        Assert.Equal(1, first.PublishedBatches);
        Assert.Equal(0, runtime.GetSession("session-b").NextSlot);
        await worker.RunAsync(1, maximumSessionTurns: 1);
        Assert.Equal(SessionStatus.Complete, runtime.GetSession("session-b").Status);
        Assert.Equal(2, server.Writes);
    }

    private static SimulationDefinition Load(JsonObject model)
    {
        var result = SimulationDefinitionLoader.Load(model.ToJsonString());
        Assert.True(result.IsValid, JsonSerializer.Serialize(result.Errors));
        return result.Definition!;
    }
}
