using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Simulation;

namespace IndustrialDataSim.Tests;

public class SkuRoutingTests
{
    internal static JsonObject Model()
    {
        var model = RuntimeFixture.Model();
        model["session"]!["endUtc"] = "2026-09-01T00:00:00.010Z";
        model["samplingIntervalMs"] = 1;
        model["session"]!["outputTags"] = JsonNode.Parse("""
            [{"name":"A","valueType":"boolean"},{"name":"B","valueType":"boolean"},
             {"name":"SKU","valueType":"string"},{"name":"Ready","valueType":"boolean"},
             {"name":"Pause","valueType":"number"},{"name":"Continue","valueType":"number"}]
            """);
        model["generators"] = JsonNode.Parse("""
            [{"tag":"Pause","kind":"booleanGate","triggerTag":"A","whenFalse":"pauseAndSuppress","pattern":{"kind":"ramp","startValue":0,"ratePerSecond":1000}},
             {"tag":"Continue","kind":"booleanGate","triggerTag":"A","whenFalse":"continueAndSuppress","pattern":{"kind":"ramp","startValue":0,"ratePerSecond":1000}},
             {"tag":"A","kind":"skuRoute","skuTag":"SKU","readyTag":"Ready","sku":"SKU-A"},
             {"tag":"B","kind":"skuRoute","skuTag":"SKU","readyTag":"Ready","sku":"SKU-B"},
             {"tag":"SKU","kind":"stringTimeline","afterSteps":"holdLast","steps":[{"value":"SKU-A","durationMs":3},{"value":"SKU-B","durationMs":3},{"value":"SKU-A","durationMs":2},{"value":"unknown","durationMs":1}]},
             {"tag":"Ready","kind":"booleanTimeline","afterSteps":"holdLast","steps":[{"value":true,"durationMs":2},{"value":false,"durationMs":2},{"value":true,"durationMs":1}]}]
            """);
        return model;
    }

    [Theory]
    [InlineData("type", "simulation.string_timeline_requires_string", ".tag")]
    [InlineData("empty", "simulation.invalid_string_steps", ".steps")]
    [InlineData("duration", "simulation.invalid_step_duration", ".durationMs")]
    [InlineData("value", "session.string_required", ".value")]
    [InlineData("after", "simulation.invalid_after_steps", ".afterSteps")]
    public void StringTimelineRejectsInvalidShapesWithoutEchoingInput(string mutation, string code, string field)
    {
        var model = Model();
        var timeline = model["generators"]![4]!;
        if (mutation == "type") model["session"]!["outputTags"]![2]!["valueType"] = "boolean";
        if (mutation == "empty") timeline["steps"] = new JsonArray();
        if (mutation == "duration") timeline["steps"]![0]!["durationMs"] = 0;
        if (mutation == "value") timeline["steps"]![0]!["value"] = true;
        if (mutation == "after") timeline["afterSteps"] = "PRIVATE_INPUT";
        var result = SimulationDefinitionLoader.Load(model.ToJsonString());
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, e => e.Code == code);
        Assert.EndsWith(field, error.Path);
        Assert.DoesNotContain("PRIVATE_INPUT", string.Join(" ", result.Errors));
    }

    [Fact]
    public void WallClockFenceCanStopAndResumeWithinOneTimestampRow()
    {
        var model = ConstantSimulationTests.Load(Model());
        var before = model.Session.StartUtc.AddTicks(-1);
        var empty = GenerationWindow.Generate(model, 0, 100, 100, 100000, before);
        Assert.Equal(0, empty.NextSlot);
        var first = GenerationWindow.Generate(model, 0, 100, 2, 100000, model.Session.StartUtc);
        Assert.Equal(2, first.NextSlot);
        var rest = GenerationWindow.Generate(model, first.NextSlot, 100, 100, 100000, model.Session.StartUtc);
        Assert.Equal(6, rest.NextSlot);
        var waiting = GenerationWindow.Generate(model, rest.NextSlot, 100, 100, 100000, model.Session.StartUtc);
        Assert.Equal(rest.NextSlot, waiting.NextSlot);
        Assert.Equal(0, waiting.PointCount);
    }

    [Fact]
    public void RoutesAreExclusiveAndGatesUseExactSkuAndReadinessBoundaries()
    {
        var result = DryRun.Generate(ConstantSimulationTests.Load(Model()));
        Assert.Empty(result.Errors);
        var data = result.Data!;
        Assert.Equal(new[] {true,true,false,false,false,false,true,true,false,false}, data["A"].Select(p => p.Value.GetBoolean()));
        Assert.Equal(new[] {false,false,false,false,true,true,false,false,false,false}, data["B"].Select(p => p.Value.GetBoolean()));
        Assert.Equal(new[] {0,1,6,7}, data["Pause"].Select(p => p.Timestamp.Millisecond));
        Assert.Equal(new[] {0d,1,2,3}, data["Pause"].Select(p => p.Value.GetDouble()));
        Assert.Equal(new[] {0d,1,6,7}, data["Continue"].Select(p => p.Value.GetDouble()));
        Assert.Equal("unknown", data["SKU"][^1].Value.GetString());
    }

    [Theory]
    [InlineData("duplicate", "simulation.duplicate_sku_route", ".sku")]
    [InlineData("sku", "simulation.invalid_sku_source", ".skuTag")]
    [InlineData("ready", "simulation.invalid_ready_source", ".readyTag")]
    [InlineData("cycle", "simulation.invalid_ready_source", ".readyTag")]
    public void InvalidRoutesFailWithSafeActionableDiagnostics(string mutation, string code, string field)
    {
        var model = Model();
        if (mutation == "duplicate") model["generators"]![3]!["sku"] = "SKU-A";
        if (mutation == "sku") model["generators"]![2]!["skuTag"] = "PRIVATE_INPUT";
        if (mutation == "ready") model["generators"]![2]!["readyTag"] = "SKU";
        if (mutation == "cycle") model["generators"]![3]!["readyTag"] = "A";
        var result = SimulationDefinitionLoader.Load(model.ToJsonString());
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, e => e.Code == code);
        Assert.EndsWith(field, error.Path);
        Assert.True(error.Message.Length > 30);
        Assert.DoesNotContain("PRIVATE_INPUT", string.Join(" ", result.Errors));
    }

    [Fact]
    public void DeclarationOrderAndWindowSizeDoNotChangeRoutedSamples()
    {
        var json = Model();
        var baseline = ConstantSimulationTests.Load(json);
        json["generators"] = new JsonArray(json["generators"]!.AsArray().Reverse().Select(n => n!.DeepClone()).ToArray());
        var reordered = ConstantSimulationTests.Load(json);
        var expected = DryRun.Generate(baseline).Data!;
        var collected = expected.Keys.ToDictionary(k => k, _ => new List<TvqPoint>());
        long cursor = 0;
        while (cursor < GenerationWindow.TotalSlots(reordered))
        {
            var window = GenerationWindow.Generate(reordered, cursor, 3, 2, 10000);
            Assert.Null(window.Error);
            Assert.True(window.NextSlot > cursor);
            cursor = window.NextSlot;
            var points = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<TvqPoint>>>(window.Payload)!;
            foreach (var (name, values) in points) collected[name].AddRange(values);
        }
        foreach (string name in expected.Keys)
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected[name]), System.Text.Json.JsonSerializer.Serialize(collected[name]));
    }
}
