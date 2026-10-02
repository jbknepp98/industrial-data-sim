using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Simulation;

namespace IndustrialDataSim.Tests;

/// <summary>
/// Proves the generator prerequisite for a future horizon-extension API. These
/// tests reload definitions with only a later end; they do not edit durable state
/// or imply that extending an admitted runtime session is already supported.
/// </summary>
public class HorizonContinuationTests
{
    public static IEnumerable<object[]> Boundaries()
    {
        string[] patterns = ["constant", "bounded-ramp", "random-staircase", "random-hold", "sequence",
            "switch", "pause-gate", "continue-gate", "closed-gate", "sku-route"];
        // Exercise mid-hold cuts, sequence/trigger boundaries and terminal holds.
        foreach (string pattern in patterns)
            foreach (int interval in new[] { 1, 5 })
                foreach (int end in new[] { 1, 6, 8, 11, 16, 32, 58, 63 })
                    yield return [pattern, interval, end];
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void CompletedShortHorizonContinuesAsTheUninterruptedLongStream(string pattern, int interval, int oldEnd)
    {
        var definition = Model(pattern, interval);
        var shortModel = LoadAtEnd(definition, oldEnd);
        var actual = Empty(shortModel);
        long cursor = Append(shortModel, 0, actual, candidateLimit: 7, pointLimit: 2);
        Assert.Equal(GenerationWindow.TotalSlots(shortModel), cursor);
        // An independent reload represents a restart; no generator instance or
        // mutable random state can be carried across this boundary accidentally.
        var extended = LoadAtEnd(definition, 96);
        long finalCursor = Append(extended, cursor, actual, candidateLimit: 13, pointLimit: 5);
        Assert.Equal(GenerationWindow.TotalSlots(extended), finalCursor);
        AssertMatchesUninterrupted(extended, actual);
        foreach (var tag in extended.Session.OutputTags)
        {
            var suffix = actual[tag.Name]!.AsArray().Where(p =>
                p!["t"]!.GetValue<DateTime>() >= shortModel.Session.EndUtc.UtcDateTime).ToArray();
            Assert.All(suffix, point => Assert.Equal(0,
                (point!["t"]!.GetValue<DateTime>() - extended.Session.StartUtc.UtcDateTime).Ticks %
                (interval * TimeSpan.TicksPerMillisecond)));
        }
    }

    [Fact]
    public void RepeatedExtensionsPreserveAPartialTimestampRowAndTheLiveFence()
    {
        var definition = Model("sku-route", 1);
        var shortModel = LoadAtEnd(definition, 4);
        var actual = Empty(shortModel);
        var partial = GenerationWindow.Generate(shortModel, 0, 1, 1, 4096);
        Assert.Equal(1, partial.NextSlot); // Six declared tags: row is unfinished.
        Merge(actual, partial);
        var middle = LoadAtEnd(definition, 11);
        var fenced = GenerationWindow.Generate(middle, partial.NextSlot, 100, 100, 4096,
            middle.Session.StartUtc);
        Assert.Equal(6, fenced.NextSlot);
        Merge(actual, fenced);
        var waiting = GenerationWindow.Generate(middle, fenced.NextSlot, 100, 100, 4096,
            middle.Session.StartUtc);
        Assert.Equal(fenced.NextSlot, waiting.NextSlot);
        Assert.Equal(0, waiting.PointCount);
        long cursor = Append(middle, fenced.NextSlot, actual, 3, 1);
        var final = LoadAtEnd(definition, 96);
        Append(final, cursor, actual, 17, 4);
        AssertMatchesUninterrupted(final, actual);
    }

    [Fact]
    public void ExtensionRetainsKnownRandomDrawsAndDoesNotRestartTheFirstHold()
    {
        var definition = Model("random-hold", 1);
        var actual = Empty(LoadAtEnd(definition, 16));
        long cursor = Append(LoadAtEnd(definition, 16), 0, actual, 7, 2);
        var final = LoadAtEnd(definition, 96);
        Append(final, cursor, actual, 13, 5);
        var values = actual["Example.Temperature"]!.AsArray().Select(p => p!["v"]!.GetValue<long>());
        // Independent seed-42 reference vector already established for this
        // generator: boundaries 11, 21, 32, 42, 52 ms, then hold the last value.
        var expected = Enumerable.Repeat(29L, 11).Concat(Enumerable.Repeat(22L, 10))
            .Concat(Enumerable.Repeat(26L, 11)).Concat(Enumerable.Repeat(22L, 10))
            .Concat(Enumerable.Repeat(25L, 54));
        Assert.Equal(expected, values);
    }

    [Fact]
    public void ExtendingAnUnalignedEndDoesNotInventSamplesOrRepeatTheTimeline()
    {
        var definition = Model("sku-route", 5);
        var shortModel = LoadAtEnd(definition, 11);
        var sameSlots = LoadAtEnd(definition, 14);
        Assert.Equal(GenerationWindow.TotalSlots(shortModel), GenerationWindow.TotalSlots(sameSlots));
        var actual = Empty(shortModel);
        long cursor = Append(shortModel, 0, actual, 7, 2);
        var noNewSample = GenerationWindow.Generate(sameSlots, cursor, 10, 10, 4096);
        Assert.Equal(cursor, noNewSample.NextSlot);
        Assert.Equal(0, noNewSample.PointCount);
        var extended = LoadAtEnd(definition, 21);
        Append(extended, cursor, actual, 13, 5);
        AssertMatchesUninterrupted(extended, actual);
        Assert.Equal(new[] { 0, 5, 10, 15, 20 }, actual["SKU"]!.AsArray()
            .Select(p => p!["t"]!.GetValue<DateTime>().Millisecond));
        Assert.All(actual["SKU"]!.AsArray().Skip(2), p => Assert.Equal("unknown", p!["v"]!.GetValue<string>()));
        // Both route gates are closed after the final unknown SKU. No gate
        // samples may appear merely because more candidate slots became legal.
        Assert.Single(actual["Pause"]!.AsArray());
        Assert.Single(actual["Continue"]!.AsArray());
    }

    private static JsonObject Model(string pattern, int interval)
    {
        var model = pattern switch
        {
            "constant" => ConstantSimulationTests.Model(),
            "bounded-ramp" => RampSimulationTests.Model(0, 1000),
            "random-hold" => RandomIntegerHoldTests.Model(),
            "random-staircase" => RandomIntegerHoldTests.Model(),
            "sequence" => SequenceSimulationTests.Model(),
            "switch" => BooleanTriggerTests.Model(),
            "sku-route" => SkuRoutingTests.Model(),
            _ => BooleanGateTests.Model(pattern == "continue-gate" ? "continueAndSuppress" : "pauseAndSuppress")
        };
        model["samplingIntervalMs"] = interval;
        if (pattern == "bounded-ramp") model["generators"]![0]!["maximum"] = 12;
        if (pattern == "random-staircase")
            model["generators"]![0] = JsonNode.Parse("""
                {"tag":"Example.Temperature","kind":"staircase","afterSteps":"holdLast",
                 "seed":42,"maxTotalDurationMs":30,"steps":[
                  {"value":0,"durationRangeMs":{"minimum":4,"maximum":15}},
                  {"value":10,"durationRangeMs":{"minimum":4,"maximum":15}},
                  {"value":20,"durationRangeMs":{"minimum":4,"maximum":15}},
                  {"value":30,"durationMs":1}]}
                """);
        if (pattern is "pause-gate" or "continue-gate")
        {
            var sequence = SequenceSimulationTests.Model()["generators"]![0]!.DeepClone().AsObject();
            sequence.Remove("tag");
            model["generators"]![0]!["pattern"] = sequence;
        }
        if (pattern == "closed-gate")
            model["generators"]![1] = JsonNode.Parse("""{"tag":"Example.Running","kind":"constant","value":false}""");
        return model;
    }

    private static SimulationDefinition LoadAtEnd(JsonObject original, int milliseconds)
    {
        var copy = original.DeepClone().AsObject();
        var start = DateTimeOffset.Parse(copy["session"]!["startUtc"]!.GetValue<string>());
        copy["session"]!["endUtc"] = start.AddMilliseconds(milliseconds).UtcDateTime.ToString("O");
        return ConstantSimulationTests.Load(copy);
    }

    private static JsonObject Empty(SimulationDefinition model)
    {
        var result = new JsonObject();
        foreach (var tag in model.Session.OutputTags) result[tag.Name] = new JsonArray();
        return result;
    }

    private static long Append(SimulationDefinition model, long cursor, JsonObject data, int candidateLimit, int pointLimit)
    {
        long total = GenerationWindow.TotalSlots(model);
        // Bounded loop makes a stuck cursor fail promptly instead of hanging CI.
        for (int turn = 0; cursor < total && turn < 10000; turn++)
        {
            var window = GenerationWindow.Generate(model, cursor, candidateLimit, pointLimit, 4096);
            Assert.Null(window.Error);
            Assert.True(window.NextSlot > cursor, "An eligible continuation failed to advance its candidate cursor.");
            Merge(data, window);
            cursor = window.NextSlot;
        }
        Assert.Equal(total, cursor);
        return cursor;
    }

    private static void Merge(JsonObject data, GenerationWindowResult window)
    {
        Assert.Null(window.Error);
        foreach (var (tag, points) in JsonNode.Parse(window.Payload)!.AsObject())
            foreach (var point in points!.AsArray()) data[tag]!.AsArray().Add(point!.DeepClone());
    }

    private static void AssertMatchesUninterrupted(SimulationDefinition model, JsonObject actual)
    {
        var expected = DryRun.Generate(model);
        Assert.Empty(expected.Errors);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(expected.Data), actual),
            "Continuing from the earlier horizon changed timestamps, values, quality, or suppression.");
        foreach (var points in actual.Select(pair => pair.Value!.AsArray()))
        {
            var times = points.Select(p => p!["t"]!.GetValue<DateTime>()).ToArray();
            Assert.Equal(times.Order().Distinct(), times);
        }
    }
}
