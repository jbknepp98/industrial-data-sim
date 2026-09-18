namespace IndustrialDataSim.Core.Validation;

/// <summary>
/// Retains at most 100 diagnostics and one omission notice. ErrorCount tracks
/// failures even after storage fills: parsers use it to detect new failures
/// before constructing definitions or performing duration arithmetic.
/// </summary>
internal sealed class ValidationErrors
{
    private const int MaximumDiagnostics = 100;
    private readonly List<ValidationError> retained = [];
    private bool omitted;

    public long ErrorCount { get; private set; }

    public void Add(ValidationError error)
    {
        ErrorCount++;
        // A nested loader may already have truncated its diagnostics. Preserve
        // that fact without retaining a second notice or reporting false success.
        if (error.Code == "validation.errors_truncated")
        {
            omitted = true;
        }
        else if (retained.Count < MaximumDiagnostics)
        {
            retained.Add(error);
        }
        else
        {
            omitted = true;
        }
    }

    public void AddRange(IEnumerable<ValidationError> errors)
    {
        foreach (var error in errors) Add(error);
    }

    public IReadOnlyList<ValidationError> AsReadOnly()
    {
        // Return a snapshot; reporting must not change the parser's error count.
        List<ValidationError> result = [.. retained];
        if (omitted)
        {
            result.Add(new("validation.errors_truncated", "$",
                "Additional validation errors were omitted; at most 100 diagnostics are shown. Correct the reported issues and rerun validation."));
        }
        return result.AsReadOnly();
    }
}
