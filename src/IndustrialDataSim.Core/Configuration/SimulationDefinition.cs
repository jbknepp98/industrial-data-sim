using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Core.Configuration;

/// <summary>
/// Executable offline model. Only the loader constructs it, after checking
/// every declared output has exactly one correctly typed generator. Keeping the
/// header nested preserves the already-published session-header v1 contract.
/// </summary>
public sealed class SimulationDefinition
{
    internal SimulationDefinition(SessionDefinition session, int samplingIntervalMs,
        IReadOnlyDictionary<string, GeneratorDefinition> generators)
    {
        Session = session;
        SamplingIntervalMs = samplingIntervalMs;
        Generators = generators;
    }

    public SessionDefinition Session { get; }
    public int SamplingIntervalMs { get; }
    public IReadOnlyDictionary<string, GeneratorDefinition> Generators { get; }
}

public sealed record SimulationLoadResult(
    SimulationDefinition? Definition, IReadOnlyList<ValidationError> Errors)
{
    public bool IsValid => Definition is not null && Errors.Count == 0;
}
