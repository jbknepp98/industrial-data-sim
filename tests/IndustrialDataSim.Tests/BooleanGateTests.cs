using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Simulation;

namespace IndustrialDataSim.Tests;

public class BooleanGateTests
{
    internal static JsonObject Model(string mode = "pauseAndSuppress")
    {
        var model = BooleanTriggerTests.Model();
        model["session"]!["endUtc"] = "2026-09-01T00:00:00.020Z";
        model["generators"]![0] = JsonNode.Parse("""
            {"tag":"Example.Temperature","kind":"booleanGate","triggerTag":"Example.Running",
             "whenFalse":"pauseAndSuppress","pattern":{"kind":"ramp","startValue":0,"ratePerSecond":1000}}
            """);
        model["generators"]![0]!["whenFalse"] = mode;
        model["generators"]![1]!["steps"] = JsonNode.Parse("""
            [{"value":false,"durationMs":2},{"value":true,"durationMs":4},
             {"value":false,"durationMs":4},{"value":true,"durationMs":3},
             {"value":false,"durationMs":3},{"value":true,"durationMs":4}]
            """);
        return model;
    }

    private static DryRunResult Run(JsonObject model) => DryRun.Generate(ConstantSimulationTests.Load(model));

    [Theory]
    [InlineData("pauseAndSuppress")]
    [InlineData("continueAndSuppress")]
    public void SuppressionOmitsAllClosedSlotsWithoutBackfill(string mode)
    {
        var result = Run(Model(mode));
        Assert.Empty(result.Errors);
        var points = result.Data!["Example.Temperature"];
        Assert.Equal(31, result.PointCount); // 20 trigger + 11 actual output points.
        Assert.Equal(new[] { 2,3,4,5,10,11,12,16,17,18,19 }, points.Select(p => p.Timestamp.Millisecond));
        double[] expected = mode == "pauseAndSuppress" ? [0,1,2,3,4,5,6,7,8,9,10] : [0,1,2,3,8,9,10,14,15,16,17];
        Assert.Equal(expected, points.Select(p => p.Value.GetDouble()));
        Assert.All(points, p => Assert.Equal(192, p.Quality));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstantGateHasPredictableEmptyOrFullOutput(bool open)
    {
        var model = Model();
        model["generators"]![1] = new JsonObject { ["tag"]="Example.Running", ["kind"]="constant", ["value"]=open };
        var result = Run(model);
        Assert.Equal(open ? 40 : 20, result.PointCount);
        Assert.Equal(open ? 20 : 0, result.Data!["Example.Temperature"].Count);
    }

    [Theory]
    [InlineData("pauseAndSuppress")]
    [InlineData("continueAndSuppress")]
    public void OffGridGateChangesDoNotDependOnSampling(string mode)
    {
        var model = Model(mode);
        var fine = Run(model).Data!["Example.Temperature"].ToDictionary(p => p.Timestamp);
        model["samplingIntervalMs"] = 5;
        var coarse = Run(model).Data!["Example.Temperature"];
        Assert.Equal(new[] { 5, 10 }, coarse.Select(p => p.Timestamp.Millisecond));
        Assert.All(coarse, p => Assert.Equal(fine[p.Timestamp].Value.GetDouble(), p.Value.GetDouble()));
    }

    [Fact]
    public void CompletedSequenceDuringSuppressionResumesWithFinalScheduledValue()
    {
        var model = Model("continueAndSuppress");
        model["generators"]![0]!["pattern"] = BooleanTriggerTests.Model()["generators"]![0]!["whenFalse"]!.DeepClone();
        var points = Run(model).Data!["Example.Temperature"];
        Assert.Equal(20, points.Single(p => p.Timestamp.Millisecond == 10).Value.GetInt64());
        Assert.All(points.Where(p => p.Timestamp.Millisecond >= 10), p => Assert.Equal(20, p.Value.GetInt64()));
    }

    [Fact]
    public void PauseResumesAtExactSequenceBoundaryWithoutRestart()
    {
        var model = Model();
        model["generators"]![0]!["pattern"] = BooleanTriggerTests.Model()["generators"]![0]!["whenFalse"]!.DeepClone();
        var points = Run(model).Data!["Example.Temperature"];
        Assert.Equal(new long[] { 0,0,10,10,20,20,20,20,20,20,20 }, points.Select(p => p.Value.GetInt64()));
    }

    [Theory]
    [InlineData("pauseAndSuppress")]
    [InlineData("continueAndSuppress")]
    public void SeededPatternUsesItsOriginalLocalSchedule(string mode)
    {
        var model = Model(mode);
        var pattern = RandomIntegerHoldTests.Model()["generators"]![0]!.DeepClone().AsObject();
        pattern.Remove("tag");
        model["generators"]![0]!["pattern"] = pattern;
        var reference = Run(RandomIntegerHoldTests.Model()).Data!["Example.Temperature"];
        var points = Run(model).Data!["Example.Temperature"];
        for (int i = 0; i < points.Count; i++)
        {
            int local = mode == "pauseAndSuppress" ? i : points[i].Timestamp.Millisecond - 2;
            Assert.Equal(reference[local].Value.GetInt64(), points[i].Value.GetInt64());
        }
    }

    [Fact]
    public void FalseTailSuppressesForeverAndEqualStatesDoNotReset()
    {
        var model = Model();
        model["generators"]![1]!["steps"] = JsonNode.Parse("""
            [{"value":true,"durationMs":2},{"value":true,"durationMs":2},{"value":false,"durationMs":1}]
            """);
        Assert.Equal(new double[] {0,1,2,3},Run(model).Data!["Example.Temperature"].Select(p=>p.Value.GetDouble()));
    }

    [Fact]
    public void ClosedGateIsNotAnArithmeticFailureButOpenOverflowStillFails()
    {
        var model = Model();
        model["generators"]![0]!["pattern"]!["startValue"] = 1e308;
        model["generators"]![0]!["pattern"]!["ratePerSecond"] = 1e308;
        model["session"]!["endUtc"] = "2026-09-01T00:00:03Z";
        model["samplingIntervalMs"] = 1000;
        model["generators"]![1] = JsonNode.Parse("""{"tag":"Example.Running","kind":"constant","value":false}""");
        Assert.Empty(Run(model).Errors);
        model["generators"]![1]!["value"] = true;
        var result = Run(model);
        Assert.Null(result.Data);
        Assert.Contains(result.Errors,e=>e.Code=="dry_run.non_finite_value");
    }

    [Fact]
    public void RepeatAndInterleavedRunsAreIdentical()
    {
        var before = JsonSerializer.Serialize(Run(Model()).Data);
        Run(Model("continueAndSuppress"));
        Assert.Equal(before,JsonSerializer.Serialize(Run(Model()).Data));
    }

    [Theory]
    [InlineData("triggerTag","\"Missing\"","simulation.invalid_trigger")]
    [InlineData("triggerTag","\"Example.Temperature\"","simulation.invalid_trigger")]
    [InlineData("whenFalse","\"restart\"","simulation.invalid_gate_mode")]
    [InlineData("pattern","null","simulation.gate_pattern_required")]
    [InlineData("pattern","{\"kind\":\"booleanGate\"}","simulation.unsupported_gate_pattern")]
    [InlineData("pattern","{\"kind\":\"constant\",\"value\":null}","simulation.invalid_constant")]
    [InlineData("pattern","{\"kind\":\"constant\",\"value\":1,\"tag\":\"Other\"}","session.unknown_property")]
    public void InvalidGateCannotExecute(string field,string json,string code)
    {
        var model=Model();model["generators"]![0]![field]=JsonNode.Parse(json);
        var result=SimulationDefinitionLoader.Load(model.ToJsonString());
        Assert.Null(result.Definition);Assert.Contains(result.Errors,e=>e.Code==code);
    }

    [Theory]
    [InlineData("triggerTag")]
    [InlineData("whenFalse")]
    [InlineData("pattern")]
    public void MissingFieldsAreRejected(string field)
    {
        var model=Model();model["generators"]![0]!.AsObject().Remove(field);
        Assert.False(SimulationDefinitionLoader.Load(model.ToJsonString()).IsValid);
    }
    [Fact]
    public void SuppressionCannotBypassCandidateWorkLimit()
    {
        var model = Model();
        model["session"]!["endUtc"] = "2026-09-01T00:00:06Z";
        model["generators"]![1] = JsonNode.Parse("""{"tag":"Example.Running","kind":"constant","value":false}""");
        var result = Run(model);
        Assert.Null(result.Data);
        Assert.Contains(result.Errors, e => e.Code == "dry_run.point_limit");
    }

    [Theory]
    [InlineData("string", "\"Ready\"")]
    [InlineData("boolean", "true")]
    public void GatedConstantsPreserveScalarType(string type, string json)
    {
        var model = Model();
        model["session"]!["outputTags"]![0]!["valueType"] = type;
        model["generators"]![0]!["pattern"] = new JsonObject { ["kind"] = "constant", ["value"] = JsonNode.Parse(json) };
        var points = Run(model).Data!["Example.Temperature"];
        Assert.Equal(11, points.Count);
        using var expected = JsonDocument.Parse(json);
        Assert.All(points, p => Assert.Equal(expected.RootElement.GetRawText(), p.Value.GetRawText()));
    }

}
