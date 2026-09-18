using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Simulation;

namespace IndustrialDataSim.Tests;

public class BooleanTriggerTests
{
    internal static JsonObject Model()
    {
        var model = SequenceSimulationTests.Model();
        model["session"]!["endUtc"] = "2026-09-01T00:00:00.024Z";
        model["session"]!["outputTags"]!.AsArray().RemoveAt(2);
        var off = JsonNode.Parse("""
            {"kind":"sequence","afterSequence":"holdLast","steps":[{"pattern":{
             "kind":"staircase","afterSteps":"holdLast","steps":[
              {"value":0,"durationMs":2},{"value":10,"durationMs":2},{"value":20,"durationMs":2}]}}]}
            """)!;
        var on = off.DeepClone();
        for (int i = 0; i < 3; i++) on["steps"]![0]!["pattern"]!["steps"]![i]!["value"] = 100 + 10 * i;
        model["generators"] = new JsonArray(
            new JsonObject { ["tag"] = "Example.Temperature", ["kind"] = "booleanSwitch",
                ["triggerTag"] = "Example.Running", ["onChange"] = "restartBranch", ["whenFalse"] = off, ["whenTrue"] = on },
            JsonNode.Parse("""
                {"tag":"Example.Running","kind":"booleanTimeline","afterSteps":"holdLast","steps":[
                {"value":false,"durationMs":8},{"value":true,"durationMs":8},{"value":false,"durationMs":8}]}
                """));
        return model;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<TvqPoint>> Data(JsonObject model)
    {
        var result = DryRun.Generate(ConstantSimulationTests.Load(model));
        Assert.Empty(result.Errors);
        return result.Data!;
    }

    private static long[] Values(JsonObject model) => Data(model)["Example.Temperature"].Select(p => p.Value.GetInt64()).ToArray();

    [Fact]
    public void BooleanStateChoosesCompletesAndRestartsDistinctBranches()
    {
        var data = Data(Model());
        long[] off = [0, 0, 10, 10, 20, 20, 20, 20];
        Assert.Equal(off.Concat(off.Select(v => v + 100)).Concat(off), data["Example.Temperature"].Select(p => p.Value.GetInt64()));
        Assert.Equal(Enumerable.Repeat(false, 8).Concat(Enumerable.Repeat(true, 8)).Concat(Enumerable.Repeat(false, 8)),
            data["Example.Running"].Select(p => p.Value.GetBoolean()));
        Assert.All(data.Values.SelectMany(p => p), p => Assert.Equal(192, p.Quality));
    }

    [Fact]
    public void InitialTrueStartsTrueBranchAtLocalZero()
    {
        var model = Model();
        model["generators"]![1]!["steps"]![0]!["value"] = true;
        Assert.Equal(100, Values(model)[0]);
    }

    [Fact]
    public void IdenticalAdjacentStatesDoNotRetrigger()
    {
        var model = Model();
        model["generators"]![1]!["steps"] = JsonNode.Parse("""
            [{"value":false,"durationMs":2},{"value":false,"durationMs":2},{"value":true,"durationMs":2}]
            """);
        Assert.Equal(new long[] { 0, 0, 10, 10, 100, 100, 110, 110, 120 }, Values(model).Take(9));
    }

    [Fact]
    public void MidSequenceChangeInterruptsAndReturningRestartsRatherThanResumes()
    {
        var model = Model();
        model["generators"]![1]!["steps"] = JsonNode.Parse("""
            [{"value":false,"durationMs":3},{"value":true,"durationMs":3},{"value":false,"durationMs":3}]
            """);
        Assert.Equal(new long[] { 0, 0, 10, 100, 100, 110, 0, 0, 10, 10, 20 }, Values(model).Take(11));
    }

    [Fact]
    public void TransitionsBetweenSamplesStillResetAtActualTime()
    {
        var model = Model();
        model["generators"]![1]!["steps"] = JsonNode.Parse("""
            [{"value":false,"durationMs":3},{"value":true,"durationMs":4},{"value":false,"durationMs":20}]
            """);
        var fine = Data(model)["Example.Temperature"].ToDictionary(p => p.Timestamp);
        model["samplingIntervalMs"] = 10;
        var coarse = Data(model)["Example.Temperature"];
        Assert.Equal(10, coarse[1].Value.GetInt64()); // false again, local time 3ms, not 10ms.
        Assert.All(coarse, p => Assert.Equal(fine[p.Timestamp].Value.GetInt64(), p.Value.GetInt64()));
    }

    [Fact]
    public void BooleanConstantIsAValidSource()
    {
        var model = Model();
        model["generators"]![1] = JsonNode.Parse("""{"tag":"Example.Running","kind":"constant","value":true}""");
        Assert.Equal(new long[] { 100, 100, 110, 110, 120 }, Values(model).Take(5));
    }

    [Fact]
    public void DeclarationOrderAndOtherRunsDoNotChangeResults()
    {
        var model = Model();
        var before = Data(model);
        var generators = model["generators"]!.AsArray();
        var first = generators[0]; generators.RemoveAt(0); generators.Add(first);
        var tags = model["session"]!["outputTags"]!.AsArray();
        var tag = tags[0]; tags.RemoveAt(0); tags.Add(tag);
        Values(Model());
        var after = Data(model);
        foreach (var name in before.Keys) Assert.Equal(JsonSerializer.Serialize(before[name]), JsonSerializer.Serialize(after[name]));
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("example.running")]
    [InlineData("Example.Temperature")]
    public void InvalidTriggerReferencesFail(string name)
    {
        var model = Model(); model["generators"]![0]!["triggerTag"] = name;
        Invalid(model, "simulation.invalid_trigger");
    }

    [Fact]
    public void NumericSourceCannotBeCoercedToBoolean()
    {
        var model = Model(); model["session"]!["outputTags"]![1]!["valueType"] = "number";
        model["generators"]![1] = JsonNode.Parse("""{"tag":"Example.Running","kind":"constant","value":1}""");
        Invalid(model, "simulation.invalid_trigger");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("\"true\"")]
    [InlineData("null")]
    public void TimelineRequiresRealBooleans(string json)
    {
        var model = Model();model["generators"]![1]!["steps"]![0]!["value"] = JsonNode.Parse(json);
        Invalid(model, "simulation.invalid_boolean");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1.0")]
    [InlineData("-1")]
    [InlineData("null")]
    [InlineData("922337203685478")]
    public void TimelineRejectsInvalidDurations(string json)
    {
        var model = Model();model["generators"]![1]!["steps"]![0]!["durationMs"] = JsonNode.Parse(json);
        Invalid(model, "simulation.invalid_step_duration");
    }

    [Fact]
    public void CumulativeTimelineOverflowFails()
    {
        var model = Model(); model["generators"]![1]!["steps"]![0]!["durationMs"] = 922337203685477L;
        Invalid(model, "simulation.invalid_step_duration");
    }

    [Theory]
    [InlineData("onChange")]
    [InlineData("triggerTag")]
    [InlineData("whenFalse")]
    [InlineData("whenTrue")]
    public void SwitchConfigurationMustBeExplicit(string field)
    {
        var model = Model();model["generators"]![0]!.AsObject().Remove(field);
        Assert.False(SimulationDefinitionLoader.Load(model.ToJsonString()).IsValid);
    }

    [Fact]
    public void UnsupportedChangePolicyFails()
    {
        var model = Model();model["generators"]![0]!["onChange"] = "resume";
        Invalid(model, "simulation.invalid_on_change");
    }

    [Fact]
    public void BranchCannotRedirectTag()
    {
        var model = Model();model["generators"]![0]!["whenTrue"]!["tag"] = "Other";
        Invalid(model, "session.unknown_property");
    }

    [Fact]
    public void NestedSwitchesAreRejectedWithoutRecursing()
    {
        var model = Model();model["generators"]![0]!["whenTrue"] = JsonNode.Parse("""{"kind":"booleanSwitch"}""");
        Invalid(model, "simulation.unsupported_switch_branch");
    }

    private static void Invalid(JsonObject model, string code)
    {
        var result = SimulationDefinitionLoader.Load(model.ToJsonString());
        Assert.Null(result.Definition);Assert.Contains(result.Errors, e => e.Code == code);
    }
}
