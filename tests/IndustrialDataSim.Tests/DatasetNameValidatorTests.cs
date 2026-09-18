using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Tests;

public class DatasetNameValidatorTests
{
    [Theory]
    [InlineData("Test")]
    [InlineData("Line-1_Shift A")]
    [InlineData("A  B")]
    [InlineData("0")]
    [InlineData("_")]
    public void AcceptsNamesAllowedByProjectPolicy(string name)
    {
        Assert.Null(DatasetNameValidator.Validate(name));
    }

    [Theory]
    [InlineData(null, "dataset.required")]
    [InlineData("", "dataset.required")]
    [InlineData("   ", "dataset.required")]
    [InlineData("\t\n", "dataset.required")]
    [InlineData(" Test", "dataset.surrounding_whitespace")]
    [InlineData("Test ", "dataset.surrounding_whitespace")]
    [InlineData("Test\n", "dataset.surrounding_whitespace")]
    [InlineData("A/B", "dataset.invalid_character")]
    [InlineData("A\\B", "dataset.invalid_character")]
    [InlineData("A.B", "dataset.invalid_character")]
    [InlineData("A%20B", "dataset.invalid_character")]
    [InlineData("A?B", "dataset.invalid_character")]
    [InlineData("A#B", "dataset.invalid_character")]
    [InlineData("A\tB", "dataset.invalid_character")]
    [InlineData("A\nB", "dataset.invalid_character")]
    [InlineData("A\0B", "dataset.invalid_character")]
    [InlineData("A\u00a0B", "dataset.invalid_character")]
    [InlineData("Caf\u00e9", "dataset.invalid_character")]
    [InlineData("A\ud83d\ude00B", "dataset.invalid_character")]
    public void RejectsInvalidNamesWithoutNormalizingThem(string? name, string code)
    {
        ValidationError error = Assert.IsType<ValidationError>(DatasetNameValidator.Validate(name));
        Assert.Equal(code, error.Code);
        Assert.Equal("$.dataset", error.Path);
    }
}
