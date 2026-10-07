using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Simulation;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Tests;

public class ManufacturingTests
{
    internal static JsonObject Model(string nodes, int seconds = 10, int sample = 1000)
    {
        var model = RuntimeFixture.Model();
        model.Remove("generators");
        model["samplingIntervalMs"] = sample;
        model["session"]!["endUtc"] = DateTimeOffset.Parse("2026-09-01T00:00:00Z").AddSeconds(seconds).UtcDateTime.ToString("O");
        model["manufacturing"] = JsonNode.Parse("{\"tickMs\":1000,\"nodes\":" + nodes + "}");
        model["session"]!["outputTags"] = new JsonArray(model["manufacturing"]!["nodes"]!.AsArray().Select(n => (JsonNode)new JsonObject
        { ["name"] = n!["tag"]!.GetValue<string>(), ["valueType"] = "number" }).ToArray());
        return model;
    }
    private static SimulationDefinition Load(JsonObject model)
    {
        var loaded = SimulationDefinitionLoader.Load(model.ToJsonString());
        Assert.True(loaded.IsValid, string.Join(";", loaded.Errors.Select(e => e.Message)));
        return loaded.Definition!;
    }
    private static double[] Values(JsonObject model, string tag) => DryRun.Generate(Load(model)).Data![tag].Select(p => p.Value.GetDouble()).ToArray();

    [Fact]
    public void DependenciesAndPreviousValuesHaveExplicitClockSemantics()
    {
        var model = Model("""[{"tag":"B","expression":{"op":"add","left":{"tag":"A"},"right":10}},{"tag":"A","expression":{"op":"add","left":{"previous":"A","initial":0},"right":1}}]""");
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (double)i), Values(model, "A"));
        Assert.Equal(Enumerable.Range(11, 10).Select(i => (double)i), Values(model, "B"));
        model["manufacturing"]!["nodes"]![1]!["expression"]!["left"] = JsonNode.Parse("""{"tag":"B"}""");
        Assert.Equal("manufacturing.cycle", Assert.Single(SimulationDefinitionLoader.Load(model.ToJsonString()).Errors).Code);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(2000)]
    [InlineData(3000)]
    [InlineData(500)]
    public void TimeIntegrationUsesProcessClockNotSampleOrTransportBatch(int sample)
    {
        var model = Model("""[{"tag":"Total","accumulator":{"mode":"time","initial":0,"ratePerSecond":10}}]""", 10, sample);
        var definition = Load(model);
        var expected = DryRun.Generate(definition).Data!["Total"];
        Assert.Equal(expected.Select(p => Math.Floor((p.Timestamp - definition.Session.StartUtc.UtcDateTime).TotalSeconds) * 10), expected.Select(p => p.Value.GetDouble()));
        for (int size = 1; size <= 7; size++)
        {
            var actual = new List<string>(); long cursor = 0;
            while (cursor < GenerationWindow.TotalSlots(definition))
            {
                // Recompilation models reopening durable state between arbitrary windows.
                var batch = GenerationWindow.Generate(Load(model), cursor, size, size, 10000);
                Assert.Null(batch.Error); Assert.True(batch.NextSlot > cursor); cursor = batch.NextSlot;
                using var json = JsonDocument.Parse(batch.Payload);
                actual.AddRange(json.RootElement.GetProperty("Total").EnumerateArray().Select(p => p.GetRawText()));
            }
            Assert.Equal(expected.Select(p => JsonSerializer.Serialize(p)), actual);
        }
    }

    [Theory]
    [InlineData("cap", 25)]
    [InlineData("wholeBatch", 30)]
    public void ProductionBatchesStopAtTargetAndRetainFinalGridObservation(string policy, double final)
    {
        var model = Model("""[{"tag":"Total","accumulator":{"mode":"batch","initial":0,"quantityRange":{"minimum":10,"maximum":10},"durationRangeMs":{"minimum":2000,"maximum":2000},"seed":4,"target":25,"finalPolicy":"cap","stopOnTarget":true}}]""", 20, 4000);
        model["manufacturing"]!["nodes"]![0]!["accumulator"]!["finalPolicy"] = policy;
        var definition = Load(model);
        Assert.Equal(new double[] { 0, 20, final }, Values(model, "Total"));
        Assert.True(definition.HasProductionTarget);
        Assert.Equal(4, definition.ProcessTrace.Count);
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(model.ToJsonString());
        Assert.Equal("extension.production_target", Assert.Throws<RuntimeFailure>(() => runtime.ExtendHorizon("session-a", definition.Session.EndUtc.AddHours(1), 0, Guid.NewGuid().ToString())).Error.Code);
    }

    [Fact]
    public void NoiseAndSensorFaultsDoNotModifyProcessDependencies()
    {
        var model = Model("""[{"tag":"Sensor","expression":{"op":"ramp","start":0,"ratePerSecond":1,"minimum":0,"maximum":20},"noise":{"amplitude":0.2,"seed":4},"faults":[{"kind":"freeze","startMs":2000,"durationMs":3000},{"kind":"quality","startMs":3000,"durationMs":2000,"quality":0}]},{"tag":"Underlying","expression":{"tag":"Sensor"}}]""");
        var points = DryRun.Generate(Load(model)).Data!;
        Assert.Equal(Enumerable.Range(0, 10).Select(i => (double)i), points["Underlying"].Select(p => p.Value.GetDouble()));
        Assert.Equal(points["Sensor"][2].Value.GetDouble(), points["Sensor"][4].Value.GetDouble());
        Assert.Equal(new[] { 192, 192, 192, 0, 0, 192, 192, 192, 192, 192 }, points["Sensor"].Select(p => p.Quality));
        var first = points["Sensor"].Select(p => p.Value.GetDouble()).ToArray();
        var nodes = model["manufacturing"]!["nodes"]!.AsArray(); var moved = nodes[0]!.DeepClone(); nodes.RemoveAt(0); nodes.Add(moved);
        Assert.Equal(first, Values(model, "Sensor"));
    }

    [Fact]
    public void RepeatingReadinessDrivesPrioritizedStateTransitionsAndHeldConditions()
    {
        var model = Model("""[{"tag":"Ready","expression":{"op":"schedule","repeat":true,"steps":[{"durationMs":2000,"value":false},{"durationMs":3000,"value":true}]}},{"tag":"State","state":{"initial":"Idle","transitions":[{"from":"*","to":"Idle","when":{"op":"not","arg":{"tag":"Ready"}}},{"from":"Idle","to":"Running","when":{"op":"held","arg":{"tag":"Ready"},"durationMs":1000}}]}}]""");
        model["session"]!["outputTags"]![0]!["valueType"] = "boolean"; model["session"]!["outputTags"]![1]!["valueType"] = "string";
        var definition = Load(model); var points = DryRun.Generate(definition).Data!;
        Assert.Equal(new[] { "Idle", "Idle", "Idle", "Running", "Running", "Idle", "Idle", "Idle", "Running", "Running" }, points["State"].Select(p => p.Value.GetString()));
        Assert.Equal(new long[] { 3000, 5000, 8000 }, definition.ProcessTrace.Select(t => t.ElapsedMs));
    }

    [Theory]
    [InlineData("rising", new[] { false, false, true, false, false, false, false, true, false, false })]
    [InlineData("falling", new[] { false, false, false, false, false, true, false, false, false, false })]
    public void EdgeConditionsEmitOneProcessTick(string operation, bool[] expected)
    {
        var model = Model("""[{"tag":"A","expression":{"op":"schedule","repeat":true,"steps":[{"durationMs":2000,"value":false},{"durationMs":3000,"value":true}]}},{"tag":"B","expression":{"op":"rising","arg":{"tag":"A"}}}]""");
        foreach (var tag in model["session"]!["outputTags"]!.AsArray()) tag!["valueType"] = "boolean";
        model["manufacturing"]!["nodes"]![1]!["expression"]!["op"] = operation;
        Assert.Equal(expected, DryRun.Generate(Load(model)).Data!["B"].Select(p => p.Value.GetBoolean()));
    }

    [Theory]
    [InlineData("{\"op\":\"divide\",\"left\":1,\"right\":0}")]
    [InlineData("{\"op\":\"if\",\"when\":true,\"then\":1,\"else\":false}")]
    [InlineData("{\"op\":\"uniform\",\"minimum\":5,\"maximum\":1,\"seed\":1,\"holdMs\":1000}")]
    [InlineData("{\"op\":\"schedule\",\"repeat\":true,\"steps\":[{\"durationMs\":1500,\"value\":1}]}")]
    [InlineData("{\"tag\":\"SECRET-DO-NOT-ECHO\"}")]
    public void InvalidProcessesReturnSafeActionableErrors(string expression)
    {
        var model = Model("[{\"tag\":\"A\",\"expression\":" + expression + "}]");
        var result = SimulationDefinitionLoader.Load(model.ToJsonString());
        Assert.False(result.IsValid); Assert.Null(result.Definition);
        var error = Assert.Single(result.Errors); Assert.StartsWith("manufacturing.", error.Code); Assert.StartsWith("$.manufacturing", error.Path);
        Assert.True(error.Message.Length > 25); Assert.DoesNotContain("SECRET-DO-NOT-ECHO", JsonSerializer.Serialize(result.Errors));
    }

    [Fact]
    public void RejectedLargeExtensionDoesNotMutateRevision()
    {
        var model = Model("""[{"tag":"A","expression":1}]""");
        using var files = new RuntimeFixture(); using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(model.ToJsonString());
        Assert.Equal("extension.model_limit", Assert.Throws<RuntimeFailure>(() => runtime.ExtendHorizon("session-a", DateTimeOffset.Parse("2026-10-01T00:00:00Z"), 0, Guid.NewGuid().ToString())).Error.Code);
        Assert.Equal(0, runtime.Horizon("session-a").Revision);
    }

    [Fact]
    public void TargetAfterLastGridPointIsRejectedInsteadOfLosingFinalTotal()
    {
        var model = Model("""[{"tag":"Total","accumulator":{"mode":"time","initial":0,"ratePerSecond":1,"target":9,"finalPolicy":"cap","stopOnTarget":true}}]""", 10, 4000);
        var error = Assert.Single(SimulationDefinitionLoader.Load(model.ToJsonString()).Errors);
        Assert.Contains("last observation grid point", error.Message);
    }

    [Fact]
    public void GatedProductionBatchPausesItsActiveElapsedTime()
    {
        var model = Model("""[{"tag":"Total","accumulator":{"mode":"batch","initial":0,"enabled":{"op":"or","args":[{"op":"lt","left":{"op":"elapsedMs"},"right":2000},{"op":"gte","left":{"op":"elapsedMs"},"right":4000}]},"quantityRange":{"minimum":10,"maximum":10},"durationRangeMs":{"minimum":3000,"maximum":3000},"seed":7}}]""");
        Assert.Equal(new double[] { 0, 0, 0, 0, 0, 10, 10, 10, 20, 20 }, Values(model, "Total"));
    }

    [Fact]
    public void InactiveScheduleTimersDoNotCountUnseenTime()
    {
        var model = Model("""[{"tag":"A","expression":{"op":"schedule","repeat":true,"steps":[{"durationMs":2000,"value":{"op":"held","arg":true,"durationMs":1000}},{"durationMs":2000,"value":false}]}}]""");
        model["session"]!["outputTags"]![0]!["valueType"] = "boolean";
        Assert.Equal(new[] { false, true, false, false, false, true, false, false, false, true }, DryRun.Generate(Load(model)).Data!["A"].Select(p => p.Value.GetBoolean()));
    }

    [Fact]
    public void StopExpressionAndTraceTruncationAreExplicit()
    {
        var model = Model("""[{"tag":"A","expression":{"op":"elapsedMs"}}]""");
        model["manufacturing"]!["stopWhen"] = JsonNode.Parse("""{"op":"gte","left":{"tag":"A"},"right":2000}""");
        Assert.Equal(new double[] { 0, 1000, 2000 }, Values(model, "A"));
        var longModel = Model("""[{"tag":"A","state":{"initial":"A","transitions":[{"from":"A","to":"B","when":true},{"from":"B","to":"A","when":true}]}}]""", 1100);
        longModel["session"]!["outputTags"]![0]!["valueType"] = "string";
        var result = Load(longModel);
        Assert.Equal(1000, result.ProcessTrace.Count); Assert.Equal(100, result.OmittedProcessTransitions);
    }

    [Fact]
    public async Task RandomProcessSurvivesDurableReopenAndExtensionWithQuality()
    {
        var model = Model("""[{"tag":"A","expression":{"op":"uniform","minimum":1,"maximum":10,"seed":123,"holdMs":1000},"faults":[{"kind":"quality","startMs":2000,"durationMs":3000,"quality":0}]}]""", 6);
        using var files = new RuntimeFixture();
        string firstPayload;
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.AddSession(model.ToJsonString()); runtime.Generate("session-a");
            firstPayload = runtime.Batches("session-a")[0].Payload!;
            await new SimulatedDelivery(runtime, new()).DeliverOneAsync("session-a");
        }
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.ExtendHorizon("session-a", DateTimeOffset.Parse("2026-09-01T00:00:10Z"), 0, Guid.NewGuid().ToString());
            runtime.Generate("session-a");
            Assert.Null(runtime.Batches("session-a")[0].Payload);
            model["session"]!["endUtc"] = "2026-09-01T00:00:10Z";
            var expected = DryRun.Generate(Load(model)).Data!["A"].Select(p => JsonSerializer.Serialize(p));
            var actual = new[] { firstPayload, runtime.Batches("session-a")[1].Payload! }.SelectMany(payload => JsonDocument.Parse(payload).RootElement.GetProperty("A").EnumerateArray().Select(p => p.GetRawText())).ToArray();
            Assert.Equal(expected, actual);
        }
    }
}
