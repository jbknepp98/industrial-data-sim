using System.Text.Json;
using IndustrialDataSim.Cli;

namespace IndustrialDataSim.Tests;

public class CliApplicationTests
{
    [Fact]
    public void ValidNameProducesSuccessfulVersionedJson()
    {
        using var output = new StringWriter();
        int exitCode = CliApplication.Run(["validate-dataset", "Line-1_Shift A"], output);
        using var response = JsonDocument.Parse(output.ToString());

        Assert.Equal(0, exitCode);
        Assert.Equal(1, response.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(response.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal(0, response.RootElement.GetProperty("errors").GetArrayLength());
    }

    [Fact]
    public void InvalidNameProducesFailureWithoutReflectingTheInput()
    {
        const string input = "sensitive/example";
        using var output = new StringWriter();
        int exitCode = CliApplication.Run(["validate-dataset", input], output);
        using var response = JsonDocument.Parse(output.ToString());

        Assert.Equal(1, exitCode);
        Assert.False(response.RootElement.GetProperty("valid").GetBoolean());
        var error = response.RootElement.GetProperty("errors")[0];
        Assert.Equal("dataset.invalid_character", error.GetProperty("code").GetString());
        Assert.Equal("$.dataset", error.GetProperty("path").GetString());
        Assert.DoesNotContain(input, output.ToString());
    }

    public static TheoryData<string[]> IncorrectArguments => new()
    {
        Array.Empty<string>(),
        new[] { "validate-dataset" },
        new[] { "validate-dataset", "Test", "extra" },
        new[] { "unsupported-command", "Test" }
    };

    [Theory]
    [MemberData(nameof(IncorrectArguments))]
    public void IncorrectUsageProducesStructuredFailure(string[] args)
    {
        using var output = new StringWriter();
        int exitCode = CliApplication.Run(args, output);
        using var response = JsonDocument.Parse(output.ToString());

        Assert.Equal(2, exitCode);
        Assert.False(response.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal("cli.usage",
            response.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
    }
}
