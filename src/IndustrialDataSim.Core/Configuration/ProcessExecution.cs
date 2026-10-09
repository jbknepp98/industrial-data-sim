using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Core.Configuration;

// Execution is owned by one loaded definition. A durable caller must restore
// its committed checkpoint before every window and persist new state in the
// same transaction as the generated cursor and output.
internal abstract class ProcessExecution
{
    internal abstract string Capture();
    internal abstract void Restore(string checkpoint);
    internal abstract long NextStep { get; }
    internal abstract long TickMs { get; }
}

public sealed class ProcessExecutionFailure(ValidationError error) : Exception(error.Message)
{
    public ValidationError Error { get; } = error;
}
