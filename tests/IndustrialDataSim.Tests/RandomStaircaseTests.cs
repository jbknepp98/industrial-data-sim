using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Simulation;

namespace IndustrialDataSim.Tests;

public class RandomStaircaseTests
{
    private static JsonObject Model(uint seed = 42)
    {
        var model = RampSimulationTests.Model();
        model["session"]!["endUtc"] = "2026-09-01T00:00:00.060Z";
        model["samplingIntervalMs"] = 1;
        model["generators"]![0] = JsonNode.Parse("""
            {"tag":"Example.Temperature","kind":"staircase","afterSteps":"holdLast",
             "seed":42,"maxTotalDurationMs":30,"steps":[
               {"value":0,"durationRangeMs":{"minimum":4,"maximum":15}},
               {"value":10,"durationRangeMs":{"minimum":4,"maximum":15}},
               {"value":20,"durationRangeMs":{"minimum":4,"maximum":15}},
               {"value":30,"durationMs":1}]}
            """);
        model["generators"]![0]!["seed"] = seed;
        return model;
    }

    private static IReadOnlyList<TvqPoint> Points(JsonObject model)
    {
        var result = DryRun.Generate(ConstantSimulationTests.Load(model));
        Assert.Empty(result.Errors);
        return result.Data!["Example.Temperature"];
    }

    [Theory]
    [InlineData(0U, 5, 7, 5)]
    [InlineData(42U, 12, 8, 8)]
    [InlineData(uint.MaxValue, 4, 4, 6)]
    public void VersionOneTimingMatchesIndependentPythonHashVectors(uint seed, int first, int second, int third)
    {
        // Reference vectors calculated with Python hashlib and little-endian
        // decoding. Assert every sampled value, including exact transitions.
        var expected = Enumerable.Repeat(0, first).Concat(Enumerable.Repeat(10, second))
            .Concat(Enumerable.Repeat(20, third)).Concat(Enumerable.Repeat(30, 60 - first - second - third));
        Assert.Equal(expected, Points(Model(seed)).Select(p => p.Value.GetInt32()));
    }

    [Fact]
    public void ManySeedsRespectEveryRangeAndReserveLaterMinima()
    {
        var schedules = new HashSet<string>();
        for (uint seed = 0; seed < 200; seed++)
        {
            var points = Points(Model(seed));
            int[] durations = new[] { 0, 10, 20 }.Select(value => points.Count(p => p.Value.GetInt32() == value)).ToArray();
            Assert.All(durations, duration => Assert.InRange(duration, 4, 15));
            Assert.InRange(durations.Sum() + 1, 13, 30);
            Assert.Equal(30, points[^1].Value.GetInt32());
            schedules.Add(string.Join(',', durations));
        }
        Assert.True(schedules.Count > 1);
    }

    [Fact]
    public void TightBudgetForcesMinimaWithoutTruncatingAnyStep()
    {
        var model = Model();
        model["generators"]![0]!["maxTotalDurationMs"] = 13;
        var values = Points(model).Select(p => p.Value.GetInt32()).ToArray();
        Assert.Equal(4, values.Count(v => v == 0));
        Assert.Equal(4, values.Count(v => v == 10));
        Assert.Equal(4, values.Count(v => v == 20));
    }

    [Fact]
    public void FixedDurationsArePreservedAmongRandomSteps()
    {
        var model = Model();
        model["generators"]![0]!["steps"]![1] = JsonNode.Parse("""{"value":10,"durationMs":7}""");
        Assert.Equal(7, Points(model).Count(p => p.Value.GetInt32() == 10));
    }

    [Fact]
    public void EqualRangeIsAnExactDwellAndLimitIsOptional()
    {
        var model = Model();
        model["generators"]![0]!.AsObject().Remove("maxTotalDurationMs");
        model["generators"]![0]!["steps"]![0]!["durationRangeMs"]!["maximum"] = 4;
        Assert.Equal(4, Points(model).Count(p => p.Value.GetInt32() == 0));
    }

    [Fact]
    public void SamplingSessionLengthAndOtherGeneratorOrderDoNotChangeSchedule()
    {
        var model = Model();
        var original = Points(model).ToDictionary(p => p.Timestamp);
        model["samplingIntervalMs"] = 3;
        model["session"]!["endUtc"] = "2026-09-01T00:00:00.030Z";
        var generators = model["generators"]!.AsArray();
        var staircase = generators[0];
        generators.RemoveAt(0);
        generators.Add(staircase);
        Points(Model(17));
        foreach (var point in Points(model))
        {
            Assert.Equal(original[point.Timestamp].Value.GetRawText(), point.Value.GetRawText());
        }
        Assert.Equal(JsonSerializer.Serialize(original.Values), JsonSerializer.Serialize(Points(Model())));
    }

    [Fact]
    public void MissingSeedFailsInsteadOfUsingWallClockRandomness()
    {
        var model = Model();
        model["generators"]![0]!.AsObject().Remove("seed");
        Invalid(model, "simulation.seed_required");
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("4294967296")]
    [InlineData("1.0")]
    [InlineData("null")]
    [InlineData("\"42\"")]
    public void InvalidSeedsFail(string value)
    {
        var model = Model();
        model["generators"]![0]!["seed"] = JsonNode.Parse(value);
        Invalid(model, "simulation.invalid_seed");
    }

    [Theory]
    [InlineData("null", "simulation.invalid_duration_range")]
    [InlineData("{}", "simulation.invalid_step_duration")]
    [InlineData("{\"minimum\":5,\"maximum\":4}", "simulation.invalid_duration_range")]
    [InlineData("{\"minimum\":0,\"maximum\":4}", "simulation.invalid_step_duration")]
    [InlineData("{\"minimum\":1.5,\"maximum\":4}", "simulation.invalid_step_duration")]
    [InlineData("{\"minimum\":1,\"maximum\":922337203685478}", "simulation.invalid_step_duration")]
    [InlineData("{\"minimum\":1,\"maximum\":4,\"noise\":true}", "session.unknown_property")]
    public void InvalidRangesFail(string value, string code)
    {
        var model = Model();
        model["generators"]![0]!["steps"]![0]!["durationRangeMs"] = JsonNode.Parse(value);
        Invalid(model, code);
    }

    [Theory]
    [InlineData("12", "simulation.infeasible_duration_limit")]
    [InlineData("0", "simulation.invalid_step_duration")]
    [InlineData("1.5", "simulation.invalid_step_duration")]
    [InlineData("null", "simulation.invalid_step_duration")]
    public void InvalidBudgetsFail(string value, string code)
    {
        var model = Model();
        model["generators"]![0]!["maxTotalDurationMs"] = JsonNode.Parse(value);
        Invalid(model, code);
    }

    [Fact]
    public void FixedAndRandomDurationCannotBothBeSupplied()
    {
        var model = Model();
        model["generators"]![0]!["steps"]![0]!["durationMs"] = 5;
        Invalid(model, "simulation.invalid_step_duration");
    }

    [Fact]
    public void SeedWithoutRandomStepsIsRejected()
    {
        var model = Model();
        model["generators"]![0]!["steps"] = JsonNode.Parse("""[{"value":0,"durationMs":10}]""");
        Invalid(model, "simulation.unused_seed");
    }

    [Fact]
    public void FixedScheduleMustAlsoFitExplicitBudget()
    {
        var model = Model();
        model["generators"]![0]!.AsObject().Remove("seed");
        model["generators"]![0]!["steps"] = JsonNode.Parse("""[{"value":0,"durationMs":31}]""");
        Invalid(model, "simulation.infeasible_duration_limit");
    }

    private static void Invalid(JsonObject model, string code)
    {
        var result = SimulationDefinitionLoader.Load(model.ToJsonString());
        Assert.Null(result.Definition);
        Assert.Contains(result.Errors, e => e.Code == code);
    }
}
