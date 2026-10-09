using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Core.Configuration;

/// <summary>
/// Executable offline model. Only the loader constructs it, after checking
/// every declared output has one generator. Windowed processes validate initial
/// types and evaluate later arithmetic while generating. Keeping the
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

    internal ProcessExecution? Execution { get; init; }
    public bool HasWindowedProcess => Execution is not null;
    public string? CaptureProcessCheckpoint() => Execution?.Capture();
    public void RestoreProcessCheckpoint(string checkpoint, long nextSlot)
    {
        if (Execution is null) throw new InvalidOperationException("Model has no windowed process.");
        Execution.Restore(checkpoint);
        long sample = nextSlot / Session.OutputTags.Count;
        long latestAllowedStep = checked(sample * SamplingIntervalMs / Execution.TickMs + 1);
        long earliestRequiredStep = nextSlot == 0 ? 0 : checked(((nextSlot - 1) / Session.OutputTags.Count) * SamplingIntervalMs / Execution.TickMs + 1);
        if (Execution.NextStep < earliestRequiredStep || Execution.NextStep > latestAllowedStep)
            throw new ProcessExecutionFailure(new("manufacturing.checkpoint_integrity", "$.manufacturing",
                "Process checkpoint clock does not match the saved sample cursor. Restore verified state; do not regenerate or edit the checkpoint."));
    }

    public bool HasProductionTarget { get; internal init; }
    public IReadOnlyList<ProcessTransition> ProcessTrace { get; internal init; } = [];
    public int OmittedProcessTransitions { get; internal init; }
    public SessionDefinition Session { get; }
    public int SamplingIntervalMs { get; }
    public IReadOnlyDictionary<string, GeneratorDefinition> Generators { get; }
}

public sealed record SimulationLoadResult(
    SimulationDefinition? Definition, IReadOnlyList<ValidationError> Errors)
{
    public bool IsValid => Definition is not null && Errors.Count == 0;
}
