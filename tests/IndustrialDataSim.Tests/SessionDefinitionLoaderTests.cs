using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Configuration;

namespace IndustrialDataSim.Tests;

public class SessionDefinitionLoaderTests
{
    internal const string ValidJson = """
        {
          "schemaVersion": 1,
          "sessionId": "synthetic-session",
          "connectionProfile": "local-profile",
          "dataset": "Example Dataset",
          "startUtc": "2026-09-01T00:00:00Z",
          "endUtc": "2026-09-02T00:00:00Z",
          "outputTags": [
            {"name": "Example.Temperature", "valueType": "number"},
            {"name": "Example.Running", "valueType": "boolean"},
            {"name": "Example.State", "valueType": "string"}
          ]
        }
        """;

    [Fact]
    public void ValidHeaderProducesTypedDefinitionWithoutChangingTagNames()
    {
        var result = SessionDefinitionLoader.Load(ValidJson);
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal("Example Dataset", result.Definition!.Dataset);
        Assert.Equal("local-profile", result.Definition.ConnectionProfile);
        Assert.Equal(TimeSpan.Zero, result.Definition.StartUtc.Offset);
        Assert.Equal(TimeSpan.FromDays(1), result.Definition.EndUtc - result.Definition.StartUtc);
        Assert.Equal(new[] { "number", "boolean", "string" }, result.Definition.OutputTags.Select(t => t.ValueType));
        Assert.Equal("Example.Temperature", result.Definition.OutputTags[0].Name);
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("sessionId")]
    [InlineData("connectionProfile")]
    [InlineData("dataset")]
    [InlineData("startUtc")]
    [InlineData("endUtc")]
    [InlineData("outputTags")]
    public void MissingRequiredFieldHasPrecisePath(string field)
    {
        var node = JsonNode.Parse(ValidJson)!.AsObject();
        node.Remove(field);
        AssertFailure(node.ToJsonString(), "session.required", "$." + field);
    }

    [Theory]
    [InlineData("sessionId", "null", "session.string_required")]
    [InlineData("dataset", "123", "session.string_required")]
    [InlineData("dataset", "\"Bad/Name\"", "dataset.invalid_character")]
    [InlineData("connectionProfile", "\"https://example.invalid\"", "session.invalid_identifier")]
    [InlineData("sessionId", "\"\"", "session.invalid_identifier")]
    [InlineData("sessionId", "\"has space\"", "session.invalid_identifier")]
    [InlineData("schemaVersion", "2", "session.unsupported_version")]
    [InlineData("schemaVersion", "1.5", "session.unsupported_version")]
    [InlineData("schemaVersion", "1.0", "session.unsupported_version")]
    [InlineData("schemaVersion", "\"1\"", "session.unsupported_version")]
    [InlineData("schemaVersion", "1e1000", "session.unsupported_version")]
    [InlineData("outputTags", "[]", "session.tags_required")]
    [InlineData("outputTags", "null", "session.tags_required")]
    [InlineData("outputTags", "{}", "session.tags_required")]
    public void InvalidFieldHasActionableError(string field, string replacement, string code)
    {
        var node = JsonNode.Parse(ValidJson)!;
        node[field] = JsonNode.Parse(replacement);
        AssertFailure(node.ToJsonString(), code, "$." + field);
    }

    [Theory]
    [InlineData("2026-09-01T00:00:00")]
    [InlineData("2026-09-01T00:00:00+00:00")]
    [InlineData("2026-09-01T00:00:00-05:00")]
    [InlineData("2026-02-30T00:00:00Z")]
    [InlineData("2026-09-01T00:00:00.12345678Z")]
    [InlineData("2026-09-01T00:00:00Z\n")]
    [InlineData("2026-09-01")]
    public void RejectsAmbiguousOrInvalidTimestamps(string timestamp)
    {
        var node = JsonNode.Parse(ValidJson)!;
        node["startUtc"] = timestamp;
        AssertFailure(node.ToJsonString(), "session.invalid_utc", "$.startUtc");
    }

    [Theory]
    [InlineData("2026-09-01T00:00:00.1Z")]
    [InlineData("2026-09-01T00:00:00.1234567Z")]
    public void PreservesSupportedTimestampPrecision(string timestamp)
    {
        var node = JsonNode.Parse(ValidJson)!;
        node["startUtc"] = timestamp;
        var result = SessionDefinitionLoader.Load(node.ToJsonString());
        Assert.True(result.IsValid);
        Assert.Equal(DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture), result.Definition!.StartUtc);
    }

    [Theory]
    [InlineData("2026-09-01T00:00:00Z")]
    [InlineData("2026-08-31T00:00:00Z")]
    public void EndMustBeLaterThanStart(string end)
    {
        var node = JsonNode.Parse(ValidJson)!;
        node["endUtc"] = end;
        AssertFailure(node.ToJsonString(), "session.invalid_time_range", "$.endUtc");
    }

    [Theory]
    [InlineData("Example.Temperature")]
    [InlineData("example.temperature")]
    public void RejectsRepeatedTagIncludingCaseOnlyVariants(string duplicate)
    {
        var node = JsonNode.Parse(ValidJson)!;
        node["outputTags"]![1]!["name"] = duplicate;
        AssertFailure(node.ToJsonString(), "session.duplicate_tag", "$.outputTags[1].name");
    }

    [Theory]
    [InlineData("null", "session.tag_object_required", "$.outputTags[0]")]
    [InlineData("{}", "session.required", "$.outputTags[0].name")]
    [InlineData("{\"name\":\"\",\"valueType\":\"number\"}", "session.invalid_tag_name", "$.outputTags[0].name")]
    [InlineData("{\"name\":\" Tag\",\"valueType\":\"number\"}", "session.invalid_tag_name", "$.outputTags[0].name")]
    [InlineData("{\"name\":\"A\\nB\",\"valueType\":\"number\"}", "session.invalid_tag_name", "$.outputTags[0].name")]
    [InlineData("{\"name\":\"Tag\",\"valueType\":\"integer\"}", "session.invalid_value_type", "$.outputTags[0].valueType")]
    [InlineData("{\"name\":\"Tag\",\"valueType\":\"number\",\"extra\":true}", "session.unknown_property", "$.outputTags[0]")]
    public void ChecksTagShapeAndType(string tagJson, string code, string path)
    {
        var node = JsonNode.Parse(ValidJson)!;
        node["outputTags"]![0] = JsonNode.Parse(tagJson);
        AssertFailure(node.ToJsonString(), code, path);
    }

    [Theory]
    [InlineData("", "session.invalid_json")]
    [InlineData("{", "session.invalid_json")]
    [InlineData("{\"schemaVersion\":1,}", "session.invalid_json")]
    [InlineData("{/* comment */}", "session.invalid_json")]
    [InlineData("[]", "session.object_required")]
    [InlineData("null", "session.object_required")]
    [InlineData("{\"sessionId\":\"\\ud800\"}", "session.invalid_json")]
    [InlineData("{\"\\ud800\":true}", "session.invalid_json")]
    public void InvalidDocumentNeverProducesAPartialDefinition(string json, string code)
    {
        AssertFailure(json, code, "$");
    }

    [Fact]
    public void RejectsDuplicatePropertyInsteadOfTakingLastValue()
    {
        string json = ValidJson.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1");
        AssertFailure(json, "session.duplicate_property", "$.schemaVersion");
    }

    [Fact]
    public void UnknownPropertyDoesNotLeakItsNameOrValue()
    {
        var node = JsonNode.Parse(ValidJson)!;
        node["private-marker"] = "private-value";
        var result = SessionDefinitionLoader.Load(node.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "session.unknown_property" && e.Path == "$");
        string errors = System.Text.Json.JsonSerializer.Serialize(result.Errors);
        Assert.DoesNotContain("private-marker", errors);
        Assert.DoesNotContain("private-value", errors);
    }

    [Fact]
    public void ExcessiveNestingReturnsStructuredFailure()
    {
        string json = new string('[', 33) + "0" + new string(']', 33);
        AssertFailure(json, "session.invalid_json", "$");
    }

    [Fact]
    public void CollectsIndependentErrorsForOneCorrectionPass()
    {
        var node = JsonNode.Parse(ValidJson)!;
        node["sessionId"] = "";
        node["dataset"] = "Bad/Name";
        node["outputTags"]![0]!["valueType"] = "unknown";
        var result = SessionDefinitionLoader.Load(node.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Equal(3, result.Errors.Count);
    }

    private static void AssertFailure(string json, string code, string path)
    {
        var result = SessionDefinitionLoader.Load(json);
        Assert.False(result.IsValid);
        Assert.Null(result.Definition);
        Assert.Contains(result.Errors, error => error.Code == code && error.Path == path);
    }
}
