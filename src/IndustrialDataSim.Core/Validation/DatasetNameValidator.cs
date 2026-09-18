namespace IndustrialDataSim.Core.Validation;

/// <summary>
/// Enforces the simulator's conservative Dataset naming policy, not an inferred
/// Historian server restriction. Validation never trims or rewrites a name:
/// silently changing it could direct writes to a different Dataset.
/// </summary>
public static class DatasetNameValidator
{
    /// <summary>
    /// Returns the first actionable failure, or null for a valid name. ASCII
    /// letters/digits, hyphens, underscores, and internal ordinary spaces are
    /// supported. There is no invented maximum length or case normalization.
    /// </summary>
    public static ValidationError? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new("dataset.required", "$.dataset", "A Dataset name is required.");
        }

        if (char.IsWhiteSpace(name[0]) || char.IsWhiteSpace(name[^1]))
        {
            return new("dataset.surrounding_whitespace", "$.dataset",
                "Dataset names must not begin or end with whitespace.");
        }

        foreach (char character in name)
        {
            // Explicit ASCII ranges avoid accepting Unicode whitespace or
            // visually similar letters that an agent might accidentally emit.
            bool allowed = character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-' or '_' or ' ';

            if (!allowed)
            {
                return new("dataset.invalid_character", "$.dataset",
                    "Use only ASCII letters, digits, hyphens, underscores, and ordinary spaces.");
            }
        }

        return null;
    }
}
