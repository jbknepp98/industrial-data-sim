using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Cli;

namespace IndustrialDataSim.Tests;

public class DryRunCommandTests
{
    [Fact]
    public void DryRunProducesHistorianShapedDataAndIsRepeatable()
    {
        var model = ConstantSimulationTests.Model();
        var first = Execute("dry-run", model);
        var second = Execute("dry-run", model);
        Assert.Equal(0, first.Code);
        Assert.Equal(first.Text, second.Text);
        using var json = JsonDocument.Parse(first.Text);
        Assert.Equal("offline", json.RootElement.GetProperty("mode").GetString());
        Assert.Equal(9, json.RootElement.GetProperty("pointCount").GetInt32());
        var data = json.RootElement.GetProperty("data");
        var point = data.GetProperty("Example.Temperature")[0];
        Assert.Equal(new[] { "t", "v", "q" }, point.EnumerateObject().Select(p => p.Name));
        Assert.Equal("2026-09-01T00:00:00Z", point.GetProperty("t").GetString());
        Assert.Equal(192, point.GetProperty("q").GetInt32());
        Assert.Equal(JsonValueKind.True, data.GetProperty("Example.Running")[0].GetProperty("v").ValueKind);
        Assert.DoesNotContain("connectionProfile", first.Text);
    }

    [Fact]
    public void ValidationDoesNotGenerateOrEchoConfiguredValues()
    {
        var node = ConstantSimulationTests.Model();
        node["generators"]![2]!["value"] = "synthetic-private-marker";
        var result = Execute("validate-simulation", node);
        Assert.Equal(0, result.Code);
        Assert.DoesNotContain("synthetic-private-marker", result.Text);
        using var json = JsonDocument.Parse(result.Text);
        Assert.False(json.RootElement.TryGetProperty("data", out _));
    }

    [Fact]
    public void FullHeaderAloneCannotAccidentallyExecute()
    {
        var header = JsonNode.Parse(SessionDefinitionLoaderTests.ValidJson)!.AsObject();
        var result = Execute("dry-run", header);
        Assert.Equal(1, result.Code);
        using var json = JsonDocument.Parse(result.Text);
        Assert.False(json.RootElement.TryGetProperty("data", out _));
    }

    [Fact]
    public void OverBudgetPreviewFailsWithoutPartialOutput()
    {
        var node = ConstantSimulationTests.Model();
        node["session"]!["endUtc"] = "2026-09-02T00:00:00Z";
        Assert.Equal(0, Execute("validate-simulation", node).Code);
        var preview = Execute("dry-run", node);
        Assert.Equal(1, preview.Code);
        AssertError(preview.Text, "dry_run.point_limit");
    }

    [Fact]
    public void EscapedStringBytesCannotBypassOutputBudget()
    {
        var node = ConstantSimulationTests.Model();
        node["generators"]![2]!["value"] = new string('<', 300_000);
        // The test input stays below the 1 MiB file cap. Escape expansion occurs
        // in the output, where three samples would exceed the 4 MiB budget.
        var preview = Execute("dry-run", node, relaxedEscaping: true);
        Assert.Equal(1, preview.Code);
        AssertError(preview.Text, "dry_run.output_limit");
        Assert.True(preview.Text.Length < 1000);
    }

    [Fact]
    public void InvalidConstantReturnsErrorBeforeAnyPoints()
    {
        var node = ConstantSimulationTests.Model();
        node["generators"]![0]!["value"] = "synthetic-private-marker";
        var result = Execute("dry-run", node);
        Assert.Equal(1, result.Code);
        Assert.DoesNotContain("synthetic-private-marker", result.Text);
        using var json = JsonDocument.Parse(result.Text);
        Assert.False(json.RootElement.TryGetProperty("data", out _));
    }

    [Fact]
    public void RampDryRunProducesNumericClampedValues()
    {
        var model = RampSimulationTests.Model();
        model["generators"]![0]!["maximum"] = 15;
        var result = Execute("dry-run", model);
        Assert.Equal(0, result.Code);
        using var json = JsonDocument.Parse(result.Text);
        var points = json.RootElement.GetProperty("data").GetProperty("Example.Temperature");
        Assert.Equal(new double[] { 10, 12, 14, 15, 15 },
            points.EnumerateArray().Select(p => p.GetProperty("v").GetDouble()));
    }

    [Fact]
    public void ArithmeticFailureDoesNotEmitPartialDataOrChangeExitContract()
    {
        var model = RampSimulationTests.Model(1e308, 1e308);
        // Configuration is finite, but evaluation over the requested range is not.
        Assert.Equal(0, Execute("validate-simulation", model).Code);
        var result = Execute("dry-run", model);
        Assert.Equal(1, result.Code);
        AssertError(result.Text, "dry_run.non_finite_value");
    }

    [Fact]
    public void InvalidSequencePolicyEmitsNoPartialCliData()
    {
        var model = SequenceSimulationTests.Model();
        model["generators"]![0]!["afterSequence"] = "loop";
        var result = Execute("dry-run", model);
        Assert.Equal(1, result.Code);
        AssertError(result.Text, "simulation.invalid_after_sequence");
    }

    private static void AssertError(string text, string code)
    {
        using var json = JsonDocument.Parse(text);
        Assert.False(json.RootElement.GetProperty("valid").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("data", out _));
        Assert.Equal(code, json.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    private static (int Code, string Text) Execute(string command, JsonObject model, bool relaxedEscaping = false)
    {
        string path = Path.GetTempFileName();
        try
        {
            var options = new JsonSerializerOptions
            {
                Encoder = relaxedEscaping ? System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping : null
            };
            File.WriteAllText(path, model.ToJsonString(options));
            using var output = new StringWriter();
            int code = CliApplication.Run([command, path], output);
            return (code, output.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
