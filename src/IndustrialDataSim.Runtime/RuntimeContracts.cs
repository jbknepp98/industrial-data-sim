using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Runtime;

public enum SessionStatus { Ready, Paused, Draining, Complete, Uncertain, Failed }
public enum BatchStatus { Pending, Sending, Acknowledged, Uncertain }

public sealed record SessionSnapshot(string SessionId, SessionStatus Status, long NextSlot,
    long TotalSlots, long QueuedPoints, long QueuedBytes, string? ErrorCode, string? ErrorMessage);
public sealed record BatchSnapshot(long Id, string SessionId, BatchStatus Status, long StartSlot,
    long EndSlot, int PointCount, int ByteCount, string Hash, string? Payload);
public sealed record TagProgress(string Tag, long? BufferedTicks, long? SubmittedTicks, long? AcknowledgedTicks);
public sealed record GenerationTurn(string SessionId, bool Progressed, string? Reason);

/// <summary>Errors are safe for callers to display. Do not attach raw database or transport exceptions.</summary>
public sealed class RuntimeFailure(string code, string message) : Exception(message)
{
    public ValidationError Error { get; } = new(code, "$", message);
}

public sealed record RuntimeLimits
{
    public int BatchPoints { get; init; } = 1000;
    public int BatchBytes { get; init; } = 1024 * 1024;
    public int CandidateSlotsPerTurn { get; init; } = 10000;
    public long SessionQueuePoints { get; init; } = 100000;
    public long GlobalQueuePoints { get; init; } = 1000000;
    public long SessionQueueBytes { get; init; } = 16 * 1024 * 1024;
    public long GlobalQueueBytes { get; init; } = 64 * 1024 * 1024;
    public long MinimumFreeDiskBytes { get; init; } = 64 * 1024 * 1024;

    internal void Validate()
    {
        if (BatchPoints is < 1 or > 10000 || BatchBytes is < 128 or > 4 * 1024 * 1024 ||
            CandidateSlotsPerTurn is < 1 or > 10000 || SessionQueuePoints < BatchPoints ||
            GlobalQueuePoints < SessionQueuePoints || SessionQueueBytes < BatchBytes ||
            GlobalQueueBytes < SessionQueueBytes || MinimumFreeDiskBytes < 0)
            throw new RuntimeFailure("runtime.invalid_limits",
                "Use 1–10000 batch points/candidate slots and 128–4194304 batch bytes. Queue limits must fit one batch; global limits must cover session limits. Disk headroom must be nonnegative.");
    }
}
