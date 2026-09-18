using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Simulation;

namespace IndustrialDataSim.Tests;

public class ConstantSimulationTests
{
    internal static JsonObject Model()
    {
        var header = JsonNode.Parse(SessionDefinitionLoaderTests.ValidJson)!;
        header["endUtc"] = "2026-09-01T00:00:03Z";
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["generatorVersion"] = 1,
            ["session"] = header,
            ["samplingIntervalMs"] = 1000,
            ["generators"] = JsonNode.Parse("""
                [
                  {"tag":"Example.Temperature","kind":"constant","value":72.5},
                  {"tag":"Example.Running","kind":"constant","value":true},
                  {"tag":"Example.State","kind":"constant","value":"Idle"}
                ]
                """)
        };
    }

    internal static SimulationDefinition Load(JsonObject model)
    {
        var result = SimulationDefinitionLoader.Load(model.ToJsonString());
        Assert.True(result.IsValid, JsonSerializer.Serialize(result.Errors));
        return result.Definition!;
    }

    [Fact]
    public void ConstantValuesSurviveParserDisposalAndRetainJsonTypes()
    {
        var model = Load(Model());
        var result = DryRun.Generate(model);
        Assert.Empty(result.Errors);
        Assert.Equal(9, result.PointCount);
        var data = result.Data!;
        Assert.Equal(72.5, data["Example.Temperature"][0].Value.GetDouble());
        Assert.True(data["Example.Running"][0].Value.GetBoolean());
        Assert.Equal("Idle", data["Example.State"][0].Value.GetString());
        foreach (var points in data.Values)
        {
            Assert.Equal(3, points.Count);
            Assert.Equal(model.Session.StartUtc.UtcDateTime, points[0].Timestamp);
            Assert.Equal(model.Session.StartUtc.UtcDateTime.AddSeconds(2), points[^1].Timestamp);
            Assert.All(points, p => { Assert.Equal(192, p.Quality); Assert.Equal(DateTimeKind.Utc, p.Timestamp.Kind); });
        }
    }

    [Fact]
    public void RepeatAndInterleavedRunsDoNotShareMutableState()
    {
        var model = Load(Model());
        string first = JsonSerializer.Serialize(DryRun.Generate(model).Data);
        var other = Model();
        other["generators"]![0]!["value"] = 999;
        DryRun.Generate(Load(other));
        Assert.Equal(first, JsonSerializer.Serialize(DryRun.Generate(model).Data));
    }

    [Fact]
    public void GeneratorDeclarationOrderDoesNotChangeOutputOrder()
    {
        var source = Model();
        var reversed = Model();
        reversed["generators"] = new JsonArray(reversed["generators"]!.AsArray().Reverse().Select(n => n!.DeepClone()).ToArray());
        Assert.Equal(JsonSerializer.Serialize(DryRun.Generate(Load(source)).Data),
            JsonSerializer.Serialize(DryRun.Generate(Load(reversed)).Data));
    }

    [Theory]
    [InlineData(1000, "2026-09-01T00:00:03Z", 3)]
    [InlineData(1000, "2026-09-01T00:00:02.0000001Z", 3)]
    [InlineData(1000, "2026-09-01T00:00:00.0000001Z", 1)]
    [InlineData(2000, "2026-09-01T00:00:03Z", 2)]
    [InlineData(2147483647, "2026-09-01T00:00:03Z", 1)]
    public void SamplingUsesHalfOpenRangeWithoutRounding(int interval, string end, int expected)
    {
        var node = Model();
        node["samplingIntervalMs"] = interval;
        node["session"]!["endUtc"] = end;
        var definition = Load(node);
        var result = DryRun.Generate(definition);
        Assert.Equal(expected * 3, result.PointCount);
        foreach (var points in result.Data!.Values)
        {
            Assert.All(points, p => Assert.True(p.Timestamp < definition.Session.EndUtc.UtcDateTime));
            for (int i = 1; i < points.Count; i++)
            {
                Assert.Equal(TimeSpan.FromMilliseconds(interval), points[i].Timestamp - points[i - 1].Timestamp);
            }
        }
    }

    [Fact]
    public void UpperDateBoundaryDoesNotOverflowAfterLastSample()
    {
        var node = Model();
        node["session"]!["startUtc"] = "9999-12-31T23:59:59Z";
        node["session"]!["endUtc"] = "9999-12-31T23:59:59.9999999Z";
        Assert.Equal(3, DryRun.Generate(Load(node)).PointCount);
    }

    [Fact]
    public void RejectsHugeRangesBeforeAllocatingOrMultiplyingPointCount()
    {
        var node = Model();
        node["samplingIntervalMs"] = 1;
        node["session"]!["startUtc"] = "0001-01-01T00:00:00Z";
        node["session"]!["endUtc"] = "9999-12-31T23:59:59Z";
        var result = DryRun.Generate(Load(node));
        Assert.Null(result.Data);
        Assert.Equal(0, result.PointCount);
        Assert.Equal("dry_run.point_limit", Assert.Single(result.Errors).Code);
    }

    [Theory]
    [InlineData(10000, true)]
    [InlineData(10001, false)]
    public void PointBudgetCountsAllTags(int points, bool allowed)
    {
        var node = Model();
        node["session"]!["outputTags"]!.AsArray().RemoveAt(2);
        node["session"]!["outputTags"]!.AsArray().RemoveAt(1);
        node["generators"]!.AsArray().RemoveAt(2);
        node["generators"]!.AsArray().RemoveAt(1);
        node["samplingIntervalMs"] = 1;
        node["session"]!["endUtc"] = $"2026-09-01T00:00:{points / 1000:00}.{points % 1000:000}Z";
        var result = DryRun.Generate(Load(node));
        Assert.Equal(allowed, result.Errors.Count == 0);
        if (allowed) Assert.Equal(points, result.PointCount);
    }

    [Theory]
    [InlineData("2026-09-01T00:55:33Z", 9999, true)]
    [InlineData("2026-09-01T00:55:34Z", 0, false)]
    public void CombinedTagsShareOnePointBudget(string end, int count, bool valid)
    {
        var node = Model();
        node["session"]!["endUtc"] = end;
        var result = DryRun.Generate(Load(node));
        Assert.Equal(valid, result.Errors.Count == 0);
        Assert.Equal(count, result.PointCount);
    }

    [Theory]
    [InlineData("samplingIntervalMs", "0", "simulation.invalid_interval")]
    [InlineData("samplingIntervalMs", "-1", "simulation.invalid_interval")]
    [InlineData("samplingIntervalMs", "1.5", "simulation.invalid_interval")]
    [InlineData("samplingIntervalMs", "2147483648", "simulation.invalid_interval")]
    [InlineData("schemaVersion", "2", "simulation.unsupported_version")]
    [InlineData("generatorVersion", "2", "simulation.unsupported_version")]
    [InlineData("generators", "[]", "simulation.generators_required")]
    [InlineData("session", "null", "session.object_required")]
    public void InvalidConfigurationDoesNotProduceExecutableModel(string field, string value, string code)
    {
        var node = Model();
        node[field] = JsonNode.Parse(value);
        AssertInvalid(node, code);
    }

    [Theory]
    [InlineData("value", "null", "simulation.invalid_constant")]
    [InlineData("value", "\"72.5\"", "simulation.invalid_constant")]
    [InlineData("value", "true", "simulation.invalid_constant")]
    [InlineData("value", "{}", "simulation.invalid_constant")]
    [InlineData("value", "1e1000", "simulation.invalid_constant")]
    [InlineData("kind", "\"sine\"", "simulation.unsupported_generator")]
    [InlineData("tag", "\"Missing.Tag\"", "simulation.unknown_tag")]
    [InlineData("tag", "\"example.temperature\"", "simulation.unknown_tag")]
    public void RejectsCoercionsUnsupportedPatternsAndUnknownReferences(string field, string value, string code)
    {
        var node = Model();
        node["generators"]![0]![field] = JsonNode.Parse(value);
        AssertInvalid(node, code);
    }

    [Fact]
    public void RequiresExactlyOneGeneratorPerDeclaredOutput()
    {
        var missing = Model();
        missing["generators"]!.AsArray().RemoveAt(0);
        AssertInvalid(missing, "simulation.missing_generator");
        var duplicate = Model();
        duplicate["generators"]!.AsArray().Add(duplicate["generators"]![0]!.DeepClone());
        AssertInvalid(duplicate, "simulation.duplicate_generator");
    }

    [Fact]
    public void PreservesLargeIntegerLiteralInsteadOfRoundingThroughDouble()
    {
        var node = Model();
        node["generators"]![0]!["value"] = JsonNode.Parse("9007199254740993");
        Assert.Equal("9007199254740993", DryRun.Generate(Load(node)).Data!["Example.Temperature"][0].Value.GetRawText());
    }

    [Fact]
    public void NestedHeaderErrorPathIdentifiesSessionField()
    {
        var node = Model();
        node["session"]!["dataset"] = "Bad/Name";
        var result = SimulationDefinitionLoader.Load(node.ToJsonString());
        Assert.Contains(result.Errors, e => e.Path == "$.session.dataset" && e.Code == "dataset.invalid_character");
    }

    private static void AssertInvalid(JsonObject node, string code)
    {
        var result = SimulationDefinitionLoader.Load(node.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Null(result.Definition);
        Assert.Contains(result.Errors, e => e.Code == code);
    }
}
