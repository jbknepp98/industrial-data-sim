using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Simulation;

namespace IndustrialDataSim.Tests;

public class StaircaseSimulationTests
{
    private static JsonObject Model()
    {
        var model = RampSimulationTests.Model();
        model["generators"]![0] = JsonNode.Parse("""
            {"tag":"Example.Temperature","kind":"staircase","afterSteps":"holdLast",
             "steps":[{"value":0,"durationMs":1000},{"value":10,"durationMs":1000},{"value":20,"durationMs":1000}]}
            """);
        return model;
    }

    private static double[] Values(JsonObject model)
    {
        var result = DryRun.Generate(ConstantSimulationTests.Load(model));
        Assert.Empty(result.Errors);
        return result.Data!["Example.Temperature"].Select(p => p.Value.GetDouble()).ToArray();
    }

    [Fact]
    public void ExactBoundariesAdvanceAndFinalValueHolds()
    {
        Assert.Equal(new double[] { 0, 10, 20, 20, 20 }, Values(Model()));
    }

    [Fact]
    public void ImmediatelyBeforeAndAtBoundariesUseHalfOpenIntervals()
    {
        var model = Model();
        model["samplingIntervalMs"] = 1;
        model["session"]!["endUtc"] = "2026-09-01T00:00:03Z";
        var values = Values(model);
        Assert.Equal(3000, values.Length);
        Assert.Equal(0, values[999]);
        Assert.Equal(10, values[1000]);
        Assert.Equal(10, values[1999]);
        Assert.Equal(20, values[2000]);
    }

    [Fact]
    public void SamplingMaySkipShortStepsWithoutAddingOffGridPoints()
    {
        var model = Model();
        model["generators"]![0]!["steps"]![0]!["durationMs"] = 100;
        model["generators"]![0]!["steps"]![1]!["durationMs"] = 100;
        Assert.Equal(new double[] { 0, 20, 20, 20, 20 }, Values(model));
    }

    [Fact]
    public void SingleStepPreservesLargeIntegerAfterParserDisposal()
    {
        var model = Model();
        model["generators"]![0]!["steps"] = JsonNode.Parse("""
            [{"value":9007199254740993,"durationMs":1}]
            """);
        var result = DryRun.Generate(ConstantSimulationTests.Load(model));
        Assert.All(result.Data!["Example.Temperature"], p => Assert.Equal(9007199254740993L, p.Value.GetInt64()));
    }

    [Fact]
    public void DescendingAndRepeatedValuesAreAllowed()
    {
        var model = Model();
        model["generators"]![0]!["steps"]![0]!["value"] = 10;
        model["generators"]![0]!["steps"]![2]!["value"] = -5;
        Assert.Equal(new double[] { 10, 10, -5, -5, -5 }, Values(model));
    }

    [Fact]
    public void SharedTimestampsAndInterleavedRunsStayDeterministic()
    {
        var model = Model();
        var definition = ConstantSimulationTests.Load(model);
        var baseline = DryRun.Generate(definition).Data!;
        model["samplingIntervalMs"] = 250;
        var fast = DryRun.Generate(ConstantSimulationTests.Load(model)).Data!["Example.Temperature"].ToDictionary(p => p.Timestamp);
        foreach (var p in baseline["Example.Temperature"])
        {
            Assert.Equal(p.Value.GetRawText(), fast[p.Timestamp].Value.GetRawText());
            Assert.Equal(192, p.Quality);
        }
        Assert.Equal(JsonSerializer.Serialize(baseline), JsonSerializer.Serialize(DryRun.Generate(definition).Data));
        Assert.True(baseline["Example.Running"][0].Value.GetBoolean());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("1.0")]
    [InlineData("null")]
    [InlineData("\"1000\"")]
    [InlineData("922337203685478")]
    public void InvalidDurationRejectsEntireModel(string json)
    {
        var model = Model();
        model["generators"]![0]!["steps"]![0]!["durationMs"] = JsonNode.Parse(json);
        Invalid(model, "simulation.invalid_step_duration");
    }

    [Fact]
    public void CumulativeDurationOverflowIsRejected()
    {
        var model = Model();
        model["generators"]![0]!["steps"]![0]!["durationMs"] = 922337203685477L;
        Invalid(model, "simulation.invalid_step_duration");
    }

    [Theory]
    [InlineData("[]", "simulation.steps_required")]
    [InlineData("null", "simulation.steps_required")]
    [InlineData("[null]", "simulation.step_object_required")]
    [InlineData("[{\"value\":0}]", "simulation.invalid_step_duration")]
    [InlineData("[{\"durationMs\":1}]", "simulation.required")]
    [InlineData("[{\"value\":true,\"durationMs\":1}]", "simulation.invalid_number")]
    [InlineData("[{\"value\":1e1000,\"durationMs\":1}]", "simulation.invalid_number")]
    [InlineData("[{\"value\":0,\"durationMs\":1,\"noise\":1}]", "session.unknown_property")]
    public void InvalidStepsAreRejected(string json, string code)
    {
        var model = Model();
        model["generators"]![0]!["steps"] = JsonNode.Parse(json);
        Invalid(model, code);
    }

    [Fact]
    public void UnsupportedEndPolicyIsRejected()
    {
        var model = Model();
        model["generators"]![0]!["afterSteps"] = "repeat";
        Invalid(model, "simulation.invalid_after_steps");
    }

    [Fact]
    public void EndPolicyMustBeExplicit()
    {
        var model = Model();
        model["generators"]![0]!.AsObject().Remove("afterSteps");
        Assert.False(SimulationDefinitionLoader.Load(model.ToJsonString()).IsValid);
    }

    [Theory]
    [InlineData("boolean")]
    [InlineData("string")]
    public void OnlyNumericOutputsAreSupported(string type)
    {
        var model = Model();
        model["session"]!["outputTags"]![0]!["valueType"] = type;
        Invalid(model, "simulation.staircase_requires_number");
    }

    private static void Invalid(JsonObject model, string code)
    {
        var result = SimulationDefinitionLoader.Load(model.ToJsonString());
        Assert.Null(result.Definition);
        Assert.Contains(result.Errors, e => e.Code == code);
    }
}
