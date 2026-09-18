using System.Text.Json;
using IndustrialDataSim.Core.Configuration;

namespace IndustrialDataSim.Tests;

public class ActionableErrorTests
{
    [Theory]
    [InlineData("minimum", "-2147483648", "2147483647", false)]
    [InlineData("maximum", "-2147483648", "2147483647", false)]
    [InlineData("seed", "0", "4294967295", false)]
    [InlineData("durationMs", "1", "922337203685477", true)]
    public void IntegerFailuresExplainBoundsWithoutEchoingInput(string field, string minimum, string maximum, bool duration)
    {
        var model = RandomIntegerHoldTests.Model();
        model["generators"]![0]![field] = "private-marker";
        var result = SimulationDefinitionLoader.Load(model.ToJsonString());
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, e => e.Path == "$.generators[0]." + field);
        Assert.Equal("simulation.invalid_integer", error.Code);
        Assert.Contains($"from {minimum} through {maximum}", error.Message);
        Assert.Equal(duration, error.Message.Contains("milliseconds"));
        Assert.Contains("Supply an integer literal", error.Message);
        Assert.DoesNotContain("private-marker", JsonSerializer.Serialize(result.Errors));
    }

    [Theory]
    [InlineData("minimum")]
    [InlineData("maximum")]
    public void NestedDurationRangeExplainsUnitsAndLocation(string bound)
    {
        var model = RandomIntegerHoldTests.Model();
        model["generators"]![0]!["holdDurationRangeMs"]![bound] = -1;
        var result = SimulationDefinitionLoader.Load(model.ToJsonString());
        var error = Assert.Single(result.Errors, e => e.Path.EndsWith(".holdDurationRangeMs." + bound));
        Assert.Equal("simulation.invalid_integer", error.Code);
        Assert.Contains("1 through 922337203685477 milliseconds", error.Message);
        Assert.DoesNotContain(result.Errors, e => e.Code == "simulation.invalid_hold_duration");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void SyntaxErrorGivesSafeOneBasedLocation()
    {
        var result = SessionDefinitionLoader.Load("{\n\"private-marker\": ]}");
        var error = Assert.Single(result.Errors);
        Assert.False(result.IsValid);
        Assert.Equal("session.invalid_json", error.Code);
        Assert.Equal("$", error.Path);
        Assert.Contains("line 2, byte", error.Message);
        Assert.Contains("1-based", error.Message);
        Assert.Contains("Correct the syntax", error.Message);
        Assert.DoesNotContain("private-marker", error.Message);
    }

    [Fact]
    public void UnicodeFailureExplainsHowToRepairWithoutEchoingInput()
    {
        var result = SessionDefinitionLoader.Load("{\"private-marker\":\"\\ud800\"}");
        var error = Assert.Single(result.Errors);
        Assert.False(result.IsValid);
        Assert.Equal("session.invalid_json", error.Code);
        Assert.Contains("Unicode", error.Message);
        Assert.Contains("Replace unpaired surrogate escapes", error.Message);
        Assert.DoesNotContain("private-marker", error.Message);
    }

    [Fact]
    public void TimelineReportsRemainingDurationInMilliseconds()
    {
        var model = BooleanTriggerTests.Model();
        model["generators"]![1]!["steps"]![1]!["durationMs"] = -1;
        var result = SimulationDefinitionLoader.Load(model.ToJsonString());
        var error = Assert.Single(result.Errors, e => e.Path == "$.generators[1].steps[1].durationMs");
        Assert.False(result.IsValid);
        Assert.Equal("simulation.invalid_step_duration", error.Code);
        Assert.Contains("1 through 922337203685469 milliseconds", error.Message);
        Assert.Contains("Shorten earlier steps", error.Message);
    }
}
