using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Simulation;

namespace IndustrialDataSim.Tests;

public class SequenceSimulationTests
{
    internal static JsonObject Model()
    {
        var model = RandomIntegerHoldTests.Model();
        model["session"]!["endUtc"] = "2026-09-01T00:00:00.064Z";
        var random = model["generators"]![0]!.DeepClone().AsObject();
        random.Remove("tag");
        model["generators"]![0] = JsonNode.Parse("""
            {"tag":"Example.Temperature","kind":"sequence","afterSequence":"holdLast","steps":[
              {"pattern":{"kind":"staircase","afterSteps":"holdLast","steps":[
                {"value":0,"durationMs":2},{"value":10,"durationMs":2},{"value":20,"durationMs":2}]}}]}
            """);
        model["generators"]![0]!["steps"]!.AsArray().Add(new JsonObject { ["pattern"] = random });
        return model;
    }

    private static IReadOnlyList<TvqPoint> Points(JsonObject model)
    {
        var result = DryRun.Generate(ConstantSimulationTests.Load(model));
        Assert.Empty(result.Errors);
        return result.Data!["Example.Temperature"];
    }

    [Fact]
    public void StaircaseHandsOffAtBoundaryAndRandomClockStartsAtZero()
    {
        var random = Points(RandomIntegerHoldTests.Model());
        var sequence = Points(Model());
        Assert.Equal(new long[] { 0, 0, 10, 10, 20, 20 }, sequence.Take(6).Select(p => p.Value.GetInt64()));
        Assert.Equal(random.Select(p => p.Value.GetInt64()), sequence.Skip(6).Take(52).Select(p => p.Value.GetInt64()));
        Assert.All(sequence.Skip(58), p => Assert.Equal(random[^1].Value.GetInt64(), p.Value.GetInt64()));
        Assert.All(sequence, p => Assert.Equal(192, p.Quality));
        Assert.Equal(64, sequence.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(63), sequence[^1].Timestamp - sequence[0].Timestamp);
    }

    [Fact]
    public void RandomStaircaseHandsOffAtResolvedDurationNotItsCap()
    {
        var model = Model();
        model["session"]!["endUtc"] = "2026-09-01T00:00:00.090Z";
        model["generators"]![0]!["steps"]![0]!["pattern"] = JsonNode.Parse("""
            {"kind":"staircase","afterSteps":"holdLast","seed":42,"maxTotalDurationMs":30,"steps":[
              {"value":0,"durationRangeMs":{"minimum":4,"maximum":15}},
              {"value":10,"durationRangeMs":{"minimum":4,"maximum":15}},
              {"value":20,"durationRangeMs":{"minimum":4,"maximum":15}},
              {"value":30,"durationMs":1}]}
            """);
        // Independent staircase vector: 12 + 8 + 8 + 1 = 29 ms, below cap 30.
        var points = Points(model);
        Assert.Equal(30, points[28].Value.GetInt64());
        Assert.Equal(Points(RandomIntegerHoldTests.Model()).Select(p => p.Value.GetInt64()),
            points.Skip(29).Take(52).Select(p => p.Value.GetInt64()));
    }

    [Fact]
    public void OffGridTransitionUsesElapsedLocalTimeWithoutResettingSampleClock()
    {
        var model = Model();
        var baseline = Points(model).ToDictionary(p => p.Timestamp);
        model["samplingIntervalMs"] = 5;
        var points = Points(model);
        Assert.Equal(13, points.Count);
        Assert.Equal(20, points[1].Value.GetInt64()); // 5ms, still first pattern.
        Assert.Equal(baseline[points[2].Timestamp].Value.GetInt64(), points[2].Value.GetInt64()); // 10ms = random local 4ms.
        Assert.All(points, p => Assert.Equal(baseline[p.Timestamp].Value.GetInt64(), p.Value.GetInt64()));
    }

    [Fact]
    public void SessionCanEndInsideFirstStepWithoutRunningLaterOutput()
    {
        var model = Model();
        model["session"]!["endUtc"] = "2026-09-01T00:00:00.003Z";
        Assert.Equal(new long[] { 0, 0, 10 }, Points(model).Select(p => p.Value.GetInt64()));
    }

    [Fact]
    public void TwoRandomStepsRestartTheirOwnSchedules()
    {
        var model = Model();
        var steps = model["generators"]![0]!["steps"]!.AsArray();
        steps[0] = steps[1]!.DeepClone();
        model["session"]!["endUtc"] = "2026-09-01T00:00:00.110Z";
        var points = Points(model);
        Assert.Equal(points.Take(52).Select(p => p.Value.GetInt64()), points.Skip(52).Take(52).Select(p => p.Value.GetInt64()));
    }

    [Fact]
    public void RepeatAndInterleavedRunsAreIdentical()
    {
        var model = Model();
        string baseline = JsonSerializer.Serialize(Points(model));
        Points(RandomIntegerHoldTests.Model(17));
        Assert.Equal(baseline, JsonSerializer.Serialize(Points(Model())));
    }

    [Fact]
    public void LastHoldDoesNotDependOnWhetherLastStepWasSampled()
    {
        var model = Model();
        model["generators"]![0]!["steps"]!.AsArray().RemoveAt(1);
        model["samplingIntervalMs"] = 10;
        var points = Points(model);
        Assert.Equal(0, points[0].Value.GetInt64());
        Assert.All(points.Skip(1), p => Assert.Equal(20, p.Value.GetInt64()));
    }

    [Theory]
    [InlineData("[]", "simulation.invalid_sequence_steps")]
    [InlineData("null", "simulation.invalid_sequence_steps")]
    [InlineData("[null]", "simulation.sequence_step_object_required")]
    [InlineData("[{}]", "simulation.sequence_pattern_required")]
    [InlineData("[{\"pattern\":null}]", "simulation.sequence_pattern_required")]
    [InlineData("[{\"pattern\":{\"kind\":\"sequence\"}}]", "simulation.unsupported_sequence_pattern")]
    [InlineData("[{\"pattern\":{\"kind\":\"ramp\"}}]", "simulation.unsupported_sequence_pattern")]
    public void InvalidStepShapeCannotExecute(string json, string code)
    {
        var model = Model();
        model["generators"]![0]!["steps"] = JsonNode.Parse(json);
        Invalid(model, code);
    }

    [Fact]
    public void ChildTagOverrideIsRejected()
    {
        var model = Model();
        model["generators"]![0]!["steps"]![0]!["pattern"]!["tag"] = "Other.Tag";
        Invalid(model, "session.unknown_property");
    }

    [Fact]
    public void UnknownStepTimingIsNotSilentlyIgnored()
    {
        var model = Model();
        model["generators"]![0]!["steps"]![0]!["durationMs"] = 100;
        Invalid(model, "session.unknown_property");
    }

    [Fact]
    public void NestedPatternsShareRandomHoldAllocationLimit()
    {
        var model = Model();
        var steps = model["generators"]![0]!["steps"]!.AsArray();
        var random = steps[1]!["pattern"]!.AsObject();
        random.Remove("holdDurationRangeMs");
        random["holdDurationMs"] = 1;
        random["durationMs"] = 6000;
        steps[0] = steps[1]!.DeepClone();
        Invalid(model, "simulation.hold_schedule_limit");
    }

    [Fact]
    public void CumulativeDurationOverflowFailsValidation()
    {
        var model = Model();
        var steps = model["generators"]![0]!["steps"]!.AsArray();
        steps[0]!["pattern"]!["steps"] = JsonNode.Parse("""[{"value":0,"durationMs":922337203685477}]""");
        Invalid(model, "simulation.sequence_duration_overflow");
    }

    [Fact]
    public void SequenceStepCountIsBounded()
    {
        var model = Model();
        var steps = model["generators"]![0]!["steps"]!.AsArray();
        while (steps.Count <= 1000) steps.Add(steps[0]!.DeepClone());
        Invalid(model, "simulation.invalid_sequence_steps");
    }

    [Fact]
    public void FinalPolicyMustBeExplicit()
    {
        var model = Model();
        model["generators"]![0]!.AsObject().Remove("afterSequence");
        Assert.False(SimulationDefinitionLoader.Load(model.ToJsonString()).IsValid);
    }

    [Fact]
    public void UnsupportedFinalPolicyIsRejected()
    {
        var model = Model();
        model["generators"]![0]!["afterSequence"] = "loop";
        Invalid(model, "simulation.invalid_after_sequence");
    }

    [Theory]
    [InlineData("string")]
    [InlineData("boolean")]
    public void SequenceIsNumericInThisIncrement(string type)
    {
        var model = Model();
        model["session"]!["outputTags"]![0]!["valueType"] = type;
        Invalid(model, "simulation.sequence_requires_number");
    }

    private static void Invalid(JsonObject model, string code)
    {
        var result = SimulationDefinitionLoader.Load(model.ToJsonString());
        Assert.Null(result.Definition);
        Assert.Contains(result.Errors, e => e.Code == code);
    }
}
