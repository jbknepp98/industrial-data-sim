using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Core.Configuration;

/// <summary>
/// Invalid input never exposes a partially usable definition. Callers must check
/// IsValid before using Definition. Error messages do not include supplied values.
/// </summary>
public sealed record SessionLoadResult(
    SessionDefinition? Definition,
    IReadOnlyList<ValidationError> Errors)
{
    public bool IsValid => Definition is not null && Errors.Count == 0;
}
