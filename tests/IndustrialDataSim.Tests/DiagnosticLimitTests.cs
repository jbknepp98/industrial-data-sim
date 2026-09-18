using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Cli;
using IndustrialDataSim.Core.Configuration;

namespace IndustrialDataSim.Tests;

public class DiagnosticLimitTests
{
    [Theory]
    [InlineData("validate-session")]
    [InlineData("validate-simulation")]
    [InlineData("dry-run")]
    public void RepeatedUnknownFieldsProduceBoundedActionableFailure(string command)
    {
        string input = "{" + string.Join(',', Enumerable.Repeat("\"x\":0", 100_000)) + "}";
        Assert.Equal(600001, Encoding.UTF8.GetByteCount(input));
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, input);
            using var output = new StringWriter();
            Assert.Equal(1, CliApplication.Run([command, path], output));
            string text = output.ToString();
            Assert.True(Encoding.UTF8.GetByteCount(text) <= 4 * 1024 * 1024);
            using var result = JsonDocument.Parse(text);
            Assert.False(result.RootElement.GetProperty("valid").GetBoolean());
            Assert.False(result.RootElement.TryGetProperty("data", out _));
            var errors = result.RootElement.GetProperty("errors");
            Assert.Equal(101, errors.GetArrayLength());
            Assert.Equal("session.unknown_property", errors[0].GetProperty("code").GetString());
            Assert.Equal("$", errors[0].GetProperty("path").GetString());
            Assert.Contains("Remove fields", errors[0].GetProperty("message").GetString());
            Assert.Equal("validation.errors_truncated", errors[100].GetProperty("code").GetString());
            Assert.Contains("rerun validation", errors[100].GetProperty("message").GetString());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(101, 101)]
    public void NoticeAppearsOnlyWhenErrorsAreOmitted(int unknownFields, int expectedDiagnostics)
    {
        var header = JsonNode.Parse(SessionDefinitionLoaderTests.ValidJson)!.AsObject();
        for (int i = 0; i < unknownFields; i++) header[$"private-marker-{i}"] = "private-value";
        var result = SessionDefinitionLoader.Load(header.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Equal(expectedDiagnostics, result.Errors.Count);
        Assert.Equal(unknownFields > 100,
            result.Errors.Any(e => e.Code == "validation.errors_truncated"));
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(result.Errors));
    }

    [Fact]
    public void FullCollectorStillRejectsMalformedRampWithoutThrowing()
    {
        var model = RampSimulationTests.Model();
        for (int i = 0; i < 110; i++) model[$"unknown{i}"] = 0;
        model["generators"]![0]!.AsObject().Remove("startValue");
        // A saturated retained count must not look like "no new errors" to
        // ReadRamp and cause it to dereference a missing start value.
        var result = SimulationDefinitionLoader.Load(model.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Null(result.Definition);
        Assert.Equal(101, result.Errors.Count);
    }

    [Fact]
    public void NestedHeaderTruncationProducesOneNotice()
    {
        var model = ConstantSimulationTests.Model();
        var session = model["session"]!.AsObject();
        for (int i = 0; i < 110; i++) session[$"unknown{i}"] = 0;
        var result = SimulationDefinitionLoader.Load(model.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Null(result.Definition);
        Assert.Equal(101, result.Errors.Count);
        Assert.Single(result.Errors, e => e.Code == "validation.errors_truncated");
        Assert.Equal("$.session", result.Errors[0].Path);
    }
}
