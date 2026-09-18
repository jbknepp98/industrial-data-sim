using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Simulation;

namespace IndustrialDataSim.Tests;

public class RandomIntegerHoldTests
{
    internal static JsonObject Model(uint seed = 42)
    {
        var model = RampSimulationTests.Model();
        model["session"]!["endUtc"] = "2026-09-01T00:00:00.052Z";
        model["samplingIntervalMs"] = 1;
        model["generators"]![0] = JsonNode.Parse("""
          {"tag":"Example.Temperature","kind":"randomIntegerHold","minimum":21,"maximum":29,
           "seed":42,"durationMs":52,"holdDurationRangeMs":{"minimum":10,"maximum":20},
           "adjacentValues":"requireChange","afterDuration":"holdLast"}
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

    [Fact]
    public void VersionOneOutputMatchesIndependentPythonReference()
    {
        // Python hashlib reference endpoints: 11, 21, 32, 42, 52 ms.
        var expected = Enumerable.Repeat(29L, 11).Concat(Enumerable.Repeat(22L, 10))
            .Concat(Enumerable.Repeat(26L, 11)).Concat(Enumerable.Repeat(22L, 10))
            .Concat(Enumerable.Repeat(25L, 10));
        Assert.Equal(expected, Points(Model()).Select(p => p.Value.GetInt64()));
    }

    [Fact]
    public void GapBetweenFeasibleTotalDurationsIsRejected()
    {
        var model = Model();
        model["generators"]![0]!["durationMs"] = 25;
        model["generators"]![0]!["holdDurationRangeMs"]!["maximum"] = 11;
        Invalid(model, "simulation.infeasible_hold_schedule");
    }

    [Fact]
    public void ManySeedsFillExactDurationWithinEveryHoldAndValueBound()
    {
        for (uint seed = 0; seed < 200; seed++)
        {
            var points = Points(Model(seed));
            Assert.Equal(52, points.Count);
            var runs = new List<int>();
            long? previous = null;
            foreach (var point in points)
            {
                long value = point.Value.GetInt64();
                Assert.InRange(value, 21, 29);
                Assert.Equal(192, point.Quality);
                if (value != previous) runs.Add(0);
                runs[^1]++;
                previous = value;
            }
            Assert.InRange(runs.Count, 3, 5);
            Assert.All(runs, length => Assert.InRange(length, 10, 20));
            Assert.Equal(52, runs.Sum());
        }
    }

    [Fact]
    public void FixedHoldsChangeExactlyAtBoundaryAndFinalValueContinues()
    {
        var model = Model();
        var generator = model["generators"]![0]!.AsObject();
        generator.Remove("holdDurationRangeMs");
        generator["holdDurationMs"] = 10;
        generator["durationMs"] = 30;
        generator["maximum"] = 22;
        var points = Points(model);
        Assert.Equal(points[0].Value.GetInt64(), points[9].Value.GetInt64());
        Assert.NotEqual(points[9].Value.GetInt64(), points[10].Value.GetInt64());
        Assert.NotEqual(points[19].Value.GetInt64(), points[20].Value.GetInt64());
        Assert.All(points.Skip(20), p => Assert.Equal(points[20].Value.GetInt64(), p.Value.GetInt64()));
    }

    [Fact]
    public void AllowRepeatSupportsSingletonRange()
    {
        var model = Model();
        model["generators"]![0]!["maximum"] = 21;
        model["generators"]![0]!["adjacentValues"] = "allowRepeat";
        Assert.All(Points(model), p => Assert.Equal(21, p.Value.GetInt64()));
    }

    [Fact]
    public void FullInt32RangeDoesNotOverflow()
    {
        var model = Model();
        model["generators"]![0]!["minimum"] = int.MinValue;
        model["generators"]![0]!["maximum"] = int.MaxValue;
        Assert.All(Points(model), p => Assert.InRange(p.Value.GetInt64(), int.MinValue, int.MaxValue));
    }

    [Fact]
    public void SamplingAndOtherRunsDoNotAlterSharedTimestampValues()
    {
        var model = Model();
        var baseline = Points(model).ToDictionary(p => p.Timestamp);
        model["samplingIntervalMs"] = 3;
        Points(Model(3));
        foreach (var point in Points(model))
            Assert.Equal(baseline[point.Timestamp].Value.GetInt64(), point.Value.GetInt64());
        Assert.Equal(JsonSerializer.Serialize(baseline.Values), JsonSerializer.Serialize(Points(Model())));
    }

    [Theory]
    [InlineData("minimum", "21.0", "simulation.invalid_integer")]
    [InlineData("minimum", "-2147483649", "simulation.invalid_integer")]
    [InlineData("maximum", "2147483648", "simulation.invalid_integer")]
    [InlineData("maximum", "20", "simulation.invalid_integer_range")]
    [InlineData("maximum", "21", "simulation.invalid_integer_range")]
    [InlineData("seed", "null", "simulation.invalid_integer")]
    [InlineData("seed", "4294967296", "simulation.invalid_integer")]
    [InlineData("durationMs", "0", "simulation.invalid_integer")]
    [InlineData("durationMs", "9", "simulation.infeasible_hold_schedule")]
    [InlineData("durationMs", "922337203685478", "simulation.invalid_integer")]
    [InlineData("adjacentValues", "\"unknown\"", "simulation.invalid_adjacent_values")]
    [InlineData("afterDuration", "\"repeat\"", "simulation.invalid_after_duration")]
    [InlineData("holdDurationRangeMs", "null", "simulation.invalid_hold_duration")]
    [InlineData("holdDurationRangeMs", "{\"minimum\":20,\"maximum\":10}", "simulation.invalid_hold_duration")]
    [InlineData("holdDurationRangeMs", "{\"minimum\":10}", "simulation.invalid_integer")]
    [InlineData("holdDurationRangeMs", "{\"minimum\":1,\"maximum\":2,\"noise\":1}", "session.unknown_property")]
    public void InvalidConfigurationFailsBeforeGenerating(string field, string json, string code)
    {
        var model = Model();
        model["generators"]![0]![field] = JsonNode.Parse(json);
        Invalid(model, code);
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("durationMs")]
    [InlineData("adjacentValues")]
    [InlineData("afterDuration")]
    [InlineData("holdDurationRangeMs")]
    public void RequiredFieldsCannotBeOmitted(string field)
    {
        var model = Model();
        model["generators"]![0]!.AsObject().Remove(field);
        Assert.False(SimulationDefinitionLoader.Load(model.ToJsonString()).IsValid);
    }

    [Fact]
    public void BothTimingFormsAreRejected()
    {
        var model = Model();
        model["generators"]![0]!["holdDurationMs"] = 10;
        Invalid(model, "simulation.invalid_hold_duration");
    }

    [Fact]
    public void FixedDurationMustDivideTotalExactly()
    {
        var model = Model();
        var generator = model["generators"]![0]!.AsObject();
        generator.Remove("holdDurationRangeMs");
        generator["holdDurationMs"] = 10;
        Invalid(model, "simulation.infeasible_hold_schedule");
    }

    [Fact]
    public void ResourceLimitRejectsScheduleBeforeAllocation()
    {
        var model = Model();
        model["generators"]![0]!["durationMs"] = 10001;
        model["generators"]![0]!["holdDurationRangeMs"] = JsonNode.Parse("""{"minimum":1,"maximum":1}""");
        Invalid(model, "simulation.infeasible_hold_schedule");
    }

    [Fact]
    public void AggregateResourceLimitCannotBeBypassedWithMoreTags()
    {
        var model = Model();
        model["generators"]![0]!["durationMs"] = 6000;
        model["generators"]![0]!["holdDurationRangeMs"] = JsonNode.Parse("""{"minimum":1,"maximum":1}""");
        model["session"]!["outputTags"]![1]!["valueType"] = "number";
        model["generators"]![1] = model["generators"]![0]!.DeepClone();
        model["generators"]![1]!["tag"] = "Example.Running";
        Invalid(model, "simulation.hold_schedule_limit");
    }

    [Theory]
    [InlineData("string")]
    [InlineData("boolean")]
    public void NumericOutputIsRequired(string type)
    {
        var model = Model();
        model["session"]!["outputTags"]![0]!["valueType"] = type;
        Invalid(model, "simulation.random_integer_requires_number");
    }

    private static void Invalid(JsonObject model, string code)
    {
        var result = SimulationDefinitionLoader.Load(model.ToJsonString());
        Assert.Null(result.Definition);
        Assert.Contains(result.Errors, e => e.Code == code);
    }
}
