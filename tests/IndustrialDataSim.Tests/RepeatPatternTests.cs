using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Simulation;

namespace IndustrialDataSim.Tests;

/// <summary>
/// afterSteps/afterDuration/afterSequence "repeat" for open-ended sessions. Fixed
/// schedules are checked against the same steps written out many times with
/// holdLast, which is independent ground truth for every dependent generator.
/// </summary>
public class RepeatPatternTests
{
    private static IReadOnlyDictionary<string, IReadOnlyList<TvqPoint>> Data(JsonObject model)
    {
        var result = DryRun.Generate(ConstantSimulationTests.Load(model));
        Assert.Empty(result.Errors);
        return result.Data!;
    }

    private static void AssertSameData(JsonObject expected, JsonObject actual)
    {
        var want = Data(expected);
        var got = Data(actual);
        Assert.Equal(want.Keys.OrderBy(k => k), got.Keys.OrderBy(k => k));
        foreach (var (tag, points) in want)
        {
            var other = got[tag];
            Assert.True(points.Count == other.Count, $"{tag}: expected {points.Count} points, got {other.Count}.");
            for (int i = 0; i < points.Count; i++)
            {
                Assert.Equal(points[i].Timestamp, other[i].Timestamp);
                Assert.Equal(points[i].Value.GetRawText(), other[i].Value.GetRawText());
                Assert.Equal(points[i].Quality, other[i].Quality);
            }
        }
    }

    /// <summary>The same steps written out <paramref name="copies"/> times, holding the last value.</summary>
    private static JsonArray Unroll(JsonNode steps, int copies)
    {
        var all = new JsonArray();
        for (int i = 0; i < copies; i++)
            foreach (var step in steps.AsArray()) all.Add(step!.DeepClone());
        return all;
    }

    private static JsonObject Generator(JsonObject model, string tag) =>
        model["generators"]!.AsArray().Single(g => g!["tag"]!.GetValue<string>() == tag)!.AsObject();

    private static string Error(JsonObject model)
    {
        var result = SimulationDefinitionLoader.Load(model.ToJsonString());
        Assert.False(result.IsValid);
        return result.Errors[0].Code;
    }

    [Fact]
    public void RepeatingRouteSourcesMatchUnrolledTimelinesIncludingGates()
    {
        // SKU repeats every 9 ms and readiness every 5 ms: the route compiles a
        // shared 45 ms cycle. Both gates follow route A across several cycles.
        var repeating = SkuRoutingTests.Model();
        repeating["session"]!["endUtc"] = "2026-09-01T00:00:00.120Z";
        var unrolled = repeating.DeepClone().AsObject();
        foreach (var tag in new[] { "SKU", "Ready" })
            Generator(repeating, tag)["afterSteps"] = "repeat";
        Generator(unrolled, "SKU")["steps"] = Unroll(Generator(unrolled, "SKU")["steps"]!, 14);
        Generator(unrolled, "Ready")["steps"] = Unroll(Generator(unrolled, "Ready")["steps"]!, 24);
        AssertSameData(unrolled, repeating);
    }

    [Fact]
    public void SwitchClockDoesNotRestartWhenTheStateMatchesAcrossACycleBoundary()
    {
        // false 8 / true 8 / false 8: the final false joins the next cycle's
        // first false, so the false branch keeps running instead of restarting.
        var repeating = BooleanTriggerTests.Model();
        repeating["session"]!["endUtc"] = "2026-09-01T00:00:00.080Z";
        var unrolled = repeating.DeepClone().AsObject();
        Generator(repeating, "Example.Running")["afterSteps"] = "repeat";
        Generator(unrolled, "Example.Running")["steps"] = Unroll(Generator(unrolled, "Example.Running")["steps"]!, 4);
        AssertSameData(unrolled, repeating);
    }

    [Theory]
    [InlineData("pauseAndSuppress")]
    [InlineData("continueAndSuppress")]
    public void GateClockAccumulatesAcrossRepeatingTriggerCycles(string mode)
    {
        var repeating = BooleanGateTests.Model(mode);
        repeating["session"]!["endUtc"] = "2026-09-01T00:00:00.075Z";
        var unrolled = repeating.DeepClone().AsObject();
        Generator(repeating, "Example.Running")["afterSteps"] = "repeat";
        Generator(unrolled, "Example.Running")["steps"] = Unroll(Generator(unrolled, "Example.Running")["steps"]!, 4);
        AssertSameData(unrolled, repeating);
    }

    [Fact]
    public void FixedStaircaseAndSequenceRepeatExactlyLikeUnrolledSchedules()
    {
        var model = RampSimulationTests.Model();
        model["session"]!["endUtc"] = "2026-09-01T00:00:00.050Z";
        model["samplingIntervalMs"] = 1;
        model["generators"]![0] = JsonNode.Parse("""
            {"tag":"Example.Temperature","kind":"staircase","afterSteps":"repeat","steps":[
              {"value":1,"durationMs":3},{"value":5,"durationMs":4}]}
            """);
        var unrolled = model.DeepClone().AsObject();
        unrolled["generators"]![0]!["afterSteps"] = "holdLast";
        unrolled["generators"]![0]!["steps"] = Unroll(model["generators"]![0]!["steps"]!, 8);
        AssertSameData(unrolled, model);

        var sequence = SequenceSimulationTests.Model();
        sequence["session"]!["endUtc"] = "2026-09-01T00:00:00.050Z";
        var fixedStep = sequence["generators"]![0]!["steps"]![0]!.DeepClone();
        sequence["generators"]![0]!["steps"] = new JsonArray(fixedStep.DeepClone(), JsonNode.Parse("""
            {"pattern":{"kind":"staircase","afterSteps":"holdLast","steps":[{"value":7,"durationMs":5}]}}
            """));
        var unrolledSequence = sequence.DeepClone().AsObject();
        sequence["generators"]![0]!["afterSequence"] = "repeat";
        unrolledSequence["generators"]![0]!["steps"] = Unroll(sequence["generators"]![0]!["steps"]!, 5);
        AssertSameData(unrolledSequence, sequence);
    }

    private static JsonObject RandomHold(string after, string end = "2026-09-01T00:00:00.260Z")
    {
        var model = RandomIntegerHoldTests.Model();
        model["session"]!["endUtc"] = end;
        model["generators"]![0]!["afterDuration"] = after;
        return model;
    }

    [Fact]
    public void RandomRepeatKeepsTheFirstCycleAndDrawsEachLaterCycleIndependently()
    {
        var held = Data(RandomHold("holdLast"))["Example.Temperature"];
        var repeated = Data(RandomHold("repeat"))["Example.Temperature"];
        // Cycle 0 (0–51 ms) is identical to the holdLast pattern.
        Assert.Equal(held.Take(52).Select(p => p.Value.GetInt64()), repeated.Take(52).Select(p => p.Value.GetInt64()));
        // Later cycles differ from cycle 0 and from each other, so data keeps moving.
        var cycles = Enumerable.Range(0, 5).Select(c => repeated.Skip(52 * c).Take(52).Select(p => p.Value.GetInt64()).ToArray()).ToArray();
        Assert.Equal(5, cycles.Select(c => string.Join(",", c)).Distinct().Count());
        foreach (var cycle in cycles)
        {
            Assert.All(cycle, v => Assert.InRange(v, 21, 29));
            // Within a cycle, holds keep their 10–20 ms bounds and requireChange.
            var runs = new List<(long Value, int Length)>();
            foreach (var v in cycle)
                if (runs.Count > 0 && runs[^1].Value == v) runs[^1] = (v, runs[^1].Length + 1);
                else runs.Add((v, 1));
            Assert.All(runs, r => Assert.InRange(r.Length, 10, 20));
            Assert.Equal(52, runs.Sum(r => r.Length));
        }
        // Deterministic: loading again reproduces every value.
        Assert.Equal(repeated.Select(p => p.Value.GetInt64()), Data(RandomHold("repeat"))["Example.Temperature"].Select(p => p.Value.GetInt64()));
    }

    [Fact]
    public void RandomStaircaseRepeatUsesMaxTotalDurationAsItsCycle()
    {
        var model = RampSimulationTests.Model();
        model["session"]!["endUtc"] = "2026-09-01T00:00:00.120Z";
        model["samplingIntervalMs"] = 1;
        model["generators"]![0] = JsonNode.Parse("""
            {"tag":"Example.Temperature","kind":"staircase","afterSteps":"repeat","seed":7,"maxTotalDurationMs":30,"steps":[
              {"value":1,"durationRangeMs":{"minimum":3,"maximum":9}},{"value":2,"durationRangeMs":{"minimum":3,"maximum":9}}]}
            """);
        var values = Data(model)["Example.Temperature"].Select(p => p.Value.GetInt64()).ToArray();
        for (int cycle = 0; cycle < 4; cycle++)
        {
            // Each 30 ms cycle starts at value 1 and ends holding value 2.
            Assert.Equal(1, values[30 * cycle]);
            Assert.Equal(2, values[30 * cycle + 29]);
        }
        var held = model.DeepClone().AsObject();
        held["generators"]![0]!["afterSteps"] = "holdLast";
        Assert.Equal(Data(held)["Example.Temperature"].Take(30).Select(p => p.Value.GetInt64()), values.Take(30));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(7, 5)]
    public void RepeatingRandomPatternIsIdenticalAcrossWindowSizes(int candidates, int points)
    {
        var definition = ConstantSimulationTests.Load(RandomHold("repeat"));
        var expected = DryRun.Generate(definition).Data!["Example.Temperature"];
        var collected = new List<long>();
        long cursor = 0, total = GenerationWindow.TotalSlots(definition);
        // A fresh load per window imitates a restart: only the cursor carries over.
        while (cursor < total)
        {
            var window = GenerationWindow.Generate(ConstantSimulationTests.Load(RandomHold("repeat")), cursor, candidates, points, 4096);
            using var payload = JsonDocument.Parse(window.Payload);
            if (payload.RootElement.TryGetProperty("Example.Temperature", out var tagPoints))
                collected.AddRange(tagPoints.EnumerateArray().Select(p => p.GetProperty("v").GetInt64()));
            Assert.True(window.NextSlot > cursor);
            cursor = window.NextSlot;
        }
        Assert.Equal(expected.Select(p => p.Value.GetInt64()), collected);
    }

    [Fact]
    public void RepeatRequiresAFixedCycleAndBelongsOnTheSequence()
    {
        var randomNoBudget = RampSimulationTests.Model();
        randomNoBudget["generators"]![0] = JsonNode.Parse("""
            {"tag":"Example.Temperature","kind":"staircase","afterSteps":"repeat","seed":7,"steps":[
              {"value":1,"durationRangeMs":{"minimum":3,"maximum":9}}]}
            """);
        Assert.Equal("simulation.repeat_needs_fixed_cycle", Error(randomNoBudget));

        var stepRepeat = SequenceSimulationTests.Model();
        stepRepeat["generators"]![0]!["steps"]![0]!["pattern"]!["afterSteps"] = "repeat";
        Assert.Equal("simulation.repeat_in_sequence_step", Error(stepRepeat));

        var mismatch = SkuRoutingTests.Model();
        Generator(mismatch, "SKU")["afterSteps"] = "repeat";
        Assert.Equal("simulation.route_repeat_mismatch", Error(mismatch));
    }
}
