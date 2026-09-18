using System.Collections;
using Microsoft.Extensions.Logging;

namespace IndustrialDataSim.Runtime;

/// <summary>
/// Only explicitly selected diagnostic fields cross the logging boundary. Never
/// add configuration, tag values, payloads, paths, or exception objects here.
/// </summary>
internal sealed record RuntimeLogEvent(
    DateTimeOffset TimestampUtc, string EventCode, string Operation,
    string Message, string Action, string? SessionId = null,
    long? BatchId = null, long? StartSlot = null, long? EndSlot = null,
    int? PointCount = null, int? ByteCount = null,
    int? OutputTagIndex = null, long? CandidateSlot = null, DateTime? SampleUtc = null)
    : IEnumerable<KeyValuePair<string, object?>>
{
    // Standard providers can preserve fields; text providers get useful prose.
    public override string ToString() =>
        $"{TimestampUtc:O} {EventCode} operation={Operation}" +
        (SessionId is null ? "" : $" session={SessionId}") +
        (BatchId is null ? "" : $" batch={BatchId}") +
        $": {Message} {Action}";

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        yield return new("timestampUtc", TimestampUtc);
        yield return new("eventCode", EventCode);
        yield return new("operation", Operation);
        yield return new("message", Message);
        yield return new("action", Action);
        yield return new("sessionId", SessionId);
        yield return new("batchId", BatchId);
        yield return new("startSlot", StartSlot);
        yield return new("endSlot", EndSlot);
        yield return new("pointCount", PointCount);
        yield return new("byteCount", ByteCount);
        yield return new("outputTagIndex", OutputTagIndex);
        yield return new("candidateSlot", CandidateSlot);
        yield return new("sampleUtc", SampleUtc);
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed partial class DurableRuntime
{
    private readonly Dictionary<string, string> blockedReasons = new(StringComparer.Ordinal);
    private long loggingFailureCount;

    /// <summary>Failures in an injected logger; delivery and SQLite remain independent.</summary>
    public long LoggingFailureCount => Interlocked.Read(ref loggingFailureCount);

    private void Log(LogLevel level, string code, string operation, string message,
        string action = "No action required.", string? sessionId = null,
        BatchSnapshot? batch = null, Core.Simulation.GenerationFailureContext? failure = null,
        long? startSlot = null, long? endSlot = null, int? pointCount = null, int? byteCount = null)
    {
        // Database contents and caller identifiers are not automatically safe.
        // Omit malformed identifiers instead of echoing arbitrary input.
        if (sessionId is not null && (sessionId.Length is < 1 or > 64 ||
            sessionId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')))
            sessionId = null;
        var entry = new RuntimeLogEvent(DateTimeOffset.UtcNow, code, operation, message, action,
            sessionId, batch?.Id, batch?.StartSlot ?? startSlot, batch?.EndSlot ?? endSlot,
            batch?.PointCount ?? pointCount, batch?.ByteCount ?? byteCount,
            failure?.OutputTagIndex, failure?.CandidateSlot, failure?.SampleUtc);
        try
        {
            if (logger.IsEnabled(level))
                logger.Log(level, new EventId(0, code), entry, null, static (state, _) => state.ToString());
        }
        catch (Exception)
        {
            // An optional observer must never change commit or delivery semantics.
            // Report once, without the provider exception (which can contain secrets).
            if (Interlocked.Increment(ref loggingFailureCount) == 1)
                try { Console.Error.WriteLine("logging.provider_failed: Operational logging failed. Check logger configuration and storage permissions; inspect SQLite for authoritative session state."); }
                catch (Exception) { /* A broken stderr must not stop the runtime either. */ }
        }
    }

    private GenerationTurn BlockGeneration(string id, string code, string message, string action)
    {
        if (!blockedReasons.TryGetValue(id, out var previous) || previous != code)
            Log(LogLevel.Warning, code, "Generate", message, action, id);
        blockedReasons[id] = code;
        return new(id, false, message + " " + action);
    }

    private void ClearGenerationBlock(string id)
    {
        if (blockedReasons.Remove(id))
            Log(LogLevel.Information, "generation.unblocked", "Generate",
                "Queue and disk checks now permit generation.", sessionId: id);
    }
}
