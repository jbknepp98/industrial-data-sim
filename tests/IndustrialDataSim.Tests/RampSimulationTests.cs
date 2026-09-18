using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Simulation;

namespace IndustrialDataSim.Tests;

public class RampSimulationTests
{
    internal static JsonObject Model(double start = 10, double rate = 2)
    {
        var model = ConstantSimulationTests.Model();
        model["session"]!["endUtc"] = "2026-09-01T00:00:05Z";
        model["generators"]![0] = new JsonObject
        {
            ["tag"] = "Example.Temperature",
            ["kind"] = "ramp",
            ["startValue"] = start,
            ["ratePerSecond"] = rate
        };
        return model;
    }

    private static double[] Values(JsonObject model)
    {
        var result = DryRun.Generate(ConstantSimulationTests.Load(model));
        Assert.Empty(result.Errors);
        return result.Data!["Example.Temperature"].Select(point => point.Value.GetDouble()).ToArray();
    }

    [Fact]
    public void AscendingRampUsesElapsedSeconds()
    {
        Assert.Equal(new double[] { 10, 12, 14, 16, 18 }, Values(Model()));
    }

    [Fact]
    public void DescendingRampCanCrossZero()
    {
        Assert.Equal(new double[] { 3, 1, -1, -3, -5 }, Values(Model(3, -2)));
    }

    [Fact]
    public void ZeroRateHoldsItsInitialValue()
    {
        Assert.Equal(new double[] { 10, 10, 10, 10, 10 }, Values(Model(10, 0)));
    }

    [Fact]
    public void UpperBoundClampsAtFirstSampleAfterCrossing()
    {
        var model = Model();
        model["generators"]![0]!["maximum"] = 15;
        Assert.Equal(new double[] { 10, 12, 14, 15, 15 }, Values(model));
    }

    [Fact]
    public void LowerBoundClampsDescendingRamp()
    {
        var model = Model(3, -2);
        model["generators"]![0]!["minimum"] = 0;
        Assert.Equal(new double[] { 3, 1, 0, 0, 0 }, Values(model));
    }

    [Fact]
    public void EqualBoundsAreAValidHold()
    {
        var model = Model();
        model["generators"]![0]!["minimum"] = 10;
        model["generators"]![0]!["maximum"] = 10;
        Assert.Equal(new double[] { 10, 10, 10, 10, 10 }, Values(model));
    }

    [Fact]
    public void StartingOnABoundCanMoveBackIntoRange()
    {
        var model = Model(10, -2);
        model["generators"]![0]!["maximum"] = 10;
        Assert.Equal(new double[] { 10, 8, 6, 4, 2 }, Values(model));
    }

    [Fact]
    public void FractionalSamplingPreservesStartPrecisionAndRate()
    {
        var model = Model(0, 0.5);
        model["samplingIntervalMs"] = 250;
        model["session"]!["startUtc"] = "2026-09-01T00:00:00.0000001Z";
        model["session"]!["endUtc"] = "2026-09-01T00:00:01.0000001Z";
        var result = DryRun.Generate(ConstantSimulationTests.Load(model));
        Assert.Equal(new double[] { 0, 0.125, 0.25, 0.375 }, Values(model));
        var points = result.Data!["Example.Temperature"];
        Assert.All(points, p => Assert.Equal(1, p.Timestamp.Ticks % TimeSpan.TicksPerMillisecond));
        Assert.All(points, p => Assert.Equal(192, p.Quality));
    }

    [Fact]
    public void ChangingSampleFrequencyDoesNotChangeSharedTimestampValues()
    {
        var slow = Model();
        var fast = Model();
        fast["samplingIntervalMs"] = 250;
        var slowPoints = DryRun.Generate(ConstantSimulationTests.Load(slow)).Data!["Example.Temperature"];
        var fastPoints = DryRun.Generate(ConstantSimulationTests.Load(fast)).Data!["Example.Temperature"]
            .ToDictionary(p => p.Timestamp);
        foreach (var point in slowPoints)
        {
            Assert.Equal(point.Value.GetDouble(), fastPoints[point.Timestamp].Value.GetDouble());
        }
    }

    [Fact]
    public void RepeatAndInterleavedRunsProduceTheSameMixedOutput()
    {
        var model = ConstantSimulationTests.Load(Model());
        string first = JsonSerializer.Serialize(DryRun.Generate(model).Data);
        DryRun.Generate(ConstantSimulationTests.Load(Model(-100, 17)));
        Assert.Equal(first, JsonSerializer.Serialize(DryRun.Generate(model).Data));
        var data = DryRun.Generate(model).Data!;
        Assert.True(data["Example.Running"][4].Value.GetBoolean());
        Assert.Equal("Idle", data["Example.State"][4].Value.GetString());
    }

    [Fact]
    public void VariedRampsAgreeWithAnIndependentDecimalAccumulator()
    {
        // A fixed test seed makes failures reproducible. This oracle uses decimal
        // per-step accumulation, independently of the engine's binary64 formula
        // evaluated from the origin. Inputs stay in ordinary process ranges.
        var random = new Random(41729);
        int[] intervals = [7, 25, 125, 250, 333, 1000];
        for (int scenario = 0; scenario < 100; scenario++)
        {
            decimal start = random.Next(-1000, 1001) / 10m;
            decimal rate = random.Next(-1000, 1001) / 100m;
            decimal lower = start - random.Next(0, 101);
            decimal upper = start + random.Next(0, 101);
            int interval = intervals[scenario % intervals.Length];
            var model = Model((double)start, (double)rate);
            model["samplingIntervalMs"] = interval;
            model["session"]!["endUtc"] = "2026-09-01T00:00:12Z";
            model["generators"]![0]!["minimum"] = lower;
            model["generators"]![0]!["maximum"] = upper;

            var result = DryRun.Generate(ConstantSimulationTests.Load(model));
            Assert.Empty(result.Errors);
            var points = result.Data!["Example.Temperature"];
            decimal accumulated = start;
            long elapsedMs = 0;
            foreach (var point in points)
            {
                decimal expected = Math.Min(upper, Math.Max(lower, accumulated));
                Assert.InRange(Math.Abs(point.Value.GetDouble() - (double)expected), 0, 1e-9);
                Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(elapsedMs), point.Timestamp);
                Assert.True(elapsedMs < 12_000);
                accumulated += rate * interval / 1000m;
                elapsedMs += interval;
            }
            Assert.True(elapsedMs >= 12_000); // The preview must cover the full grid.
        }
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    public void LocaleDoesNotChangeParsingOrSerializedPoints(string locale)
    {
        var model = Model(1.25, 0.125);
        string baseline = JsonSerializer.Serialize(DryRun.Generate(ConstantSimulationTests.Load(model)).Data);
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        var previousUiCulture = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new(locale);
            System.Globalization.CultureInfo.CurrentUICulture = new(locale);
            string localized = JsonSerializer.Serialize(DryRun.Generate(ConstantSimulationTests.Load(model)).Data);
            Assert.Equal(baseline, localized);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
            System.Globalization.CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Theory]
    [InlineData("startValue", "null", "simulation.invalid_number")]
    [InlineData("ratePerSecond", "\"2\"", "simulation.invalid_number")]
    [InlineData("ratePerSecond", "true", "simulation.invalid_number")]
    [InlineData("minimum", "null", "simulation.invalid_number")]
    [InlineData("maximum", "{}", "simulation.invalid_number")]
    [InlineData("startValue", "1e1000", "simulation.invalid_number")]
    [InlineData("ratePerSecond", "-1e1000", "simulation.invalid_number")]
    [InlineData("maximum", "1e1000", "simulation.invalid_number")]
    [InlineData("minimum", "11", "simulation.start_outside_bounds")]
    [InlineData("maximum", "9", "simulation.start_outside_bounds")]
    public void InvalidNumericConfigurationCannotExecute(string field, string json, string code)
    {
        var model = Model();
        model["generators"]![0]![field] = JsonNode.Parse(json);
        AssertInvalid(model, code);
    }

    [Theory]
    [InlineData("startValue")]
    [InlineData("ratePerSecond")]
    public void RequiredParametersCannotBeOmitted(string field)
    {
        var model = Model();
        model["generators"]![0]!.AsObject().Remove(field);
        var result = SimulationDefinitionLoader.Load(model.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "simulation.required" && e.Path == "$.generators[0]." + field);
    }

    [Theory]
    [InlineData("boolean")]
    [InlineData("string")]
    public void RampRequiresNumericOutput(string type)
    {
        var model = Model();
        model["session"]!["outputTags"]![0]!["valueType"] = type;
        AssertInvalid(model, "simulation.ramp_requires_number");
    }

    [Fact]
    public void ReversedBoundsAreRejected()
    {
        var model = Model();
        model["generators"]![0]!["minimum"] = 11;
        model["generators"]![0]!["maximum"] = 9;
        AssertInvalid(model, "simulation.invalid_bounds");
    }

    [Fact]
    public void ConstantOnlyFieldsAreNotSilentlyIgnoredOnRamps()
    {
        var model = Model();
        model["generators"]![0]!["value"] = 10;
        AssertInvalid(model, "session.unknown_property");
    }

    [Fact]
    public void RampOnlyFieldsAreNotSilentlyIgnoredOnConstants()
    {
        var model = ConstantSimulationTests.Model();
        model["generators"]![0]!["ratePerSecond"] = 2;
        AssertInvalid(model, "session.unknown_property");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverflowRejectsEntirePreviewEvenWhenABoundCouldHideIt(bool bounded)
    {
        var model = Model(1e308, 1e308);
        if (bounded) model["generators"]![0]!["maximum"] = 1.1e308;
        var result = DryRun.Generate(ConstantSimulationTests.Load(model));
        Assert.Null(result.Data);
        Assert.Equal(0, result.PointCount);
        Assert.Equal("dry_run.non_finite_value", Assert.Single(result.Errors).Code);
        Assert.Equal("$.session.outputTags[0].name", result.Errors[0].Path);
    }

    private static void AssertInvalid(JsonObject model, string code)
    {
        var result = SimulationDefinitionLoader.Load(model.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Null(result.Definition);
        Assert.Contains(result.Errors, e => e.Code == code);
    }
}
