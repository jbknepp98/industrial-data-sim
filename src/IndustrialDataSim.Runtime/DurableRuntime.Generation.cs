using System.Text;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using IndustrialDataSim.Core.Simulation;

namespace IndustrialDataSim.Runtime;

public sealed partial class DurableRuntime
{
    /// <summary>One bounded turn per Ready session, rotating who receives scarce queue space first.</summary>
    public IReadOnlyList<GenerationTurn> GenerateRound() => Access(() =>
    {
        var ids = Rotate(SessionIds("Ready"), generationAfter);
        if (ids.Count > 0) generationAfter = ids[0];
        var turns = new List<GenerationTurn>();
        foreach (string id in ids)
        {
            try { turns.Add(Generate(id)); }
            catch (RuntimeFailure failure) when (failure.Error.Code == "runtime.configuration_integrity")
            {
                // LoadModelForWork already persisted Failed and preserved ownership.
                // Do not broaden this catch: storage and programming failures must
                // abort the round rather than appearing to be isolated safely.
                turns.Add(new(id, false, failure.Error.Message));
            }
        }
        return turns;
    });

    public GenerationTurn Generate(string id) => Access(() =>
    {
        var session = ReadSession(id);
        if (session.Status != SessionStatus.Ready) return new GenerationTurn(id, false, "Session is not Ready; inspect its status before generating.");
        var usage = QueueUsage();
        // Reserve capacity for a full bounded batch before generation. This is
        // deliberately conservative; it avoids allocating data the queue cannot hold.
        if (session.QueuedPoints > limits.SessionQueuePoints - limits.BatchPoints ||
            usage.Points > limits.GlobalQueuePoints - limits.BatchPoints ||
            session.QueuedBytes > limits.SessionQueueBytes - limits.BatchBytes ||
            usage.Bytes > limits.GlobalQueueBytes - limits.BatchBytes)
            return BlockGeneration(id, "generation.queue_full", "Queue limit reached; capacity is insufficient for another bounded batch; the checkpoint has not advanced.", "Deliver pending work or review queue limits. Inspect Uncertain sessions if delivery is blocked.");
        long free = FreeDiskBytes?.Invoke() ?? AvailableDiskBytes();
        if (free < limits.MinimumFreeDiskBytes || free - limits.MinimumFreeDiskBytes < limits.BatchBytes * 4L)
            return BlockGeneration(id, "generation.disk_low", "Disk headroom is low; the checkpoint has not advanced.", "Free space on the state volume before continuing.");
        ClearGenerationBlock(id);
        var model = LoadModelForWork(id);
        if (session.TotalSlots != GenerationWindow.TotalSlots(model))
            throw StateIntegrityFailure();
        var window = GenerationWindow.Generate(model, session.NextSlot, limits.CandidateSlotsPerTurn, limits.BatchPoints, limits.BatchBytes);
        if (window.Error is { } error)
        {
            Execute("UPDATE sessions SET state='Failed',error_code=$code,error_message=$message WHERE id=$id",
                ("$id", id), ("$code", error.Code), ("$message", error.Message));
            Log(LogLevel.Error, error.Code, "Generate", error.Message,
                error.Code == "generation.point_too_large"
                    ? "Session is Failed. Reopen with larger batch and compatible queue byte limits, then call RetryGeneration; preserve the configuration and checkpoint."
                    : "Session is Failed. Preserve its checkpoints and inspect the indicated configuration entry; Resume only supports Paused sessions.",
                id, failure: window.FailureContext);
            return new(id, false, error.Message);
        }
        bool completed = false;
        InTransaction(() =>
        {
            if (window.PointCount > 0)
            {
                Execute("""
                    INSERT INTO batches(session_id,state,start_slot,end_slot,point_count,byte_count,payload,hash,positions)
                    VALUES($id,'Pending',$start,$end,$count,$bytes,$payload,$hash,$positions)
                    """, ("$id", id), ("$start", session.NextSlot), ("$end", window.NextSlot), ("$count", window.PointCount),
                    ("$bytes", Encoding.UTF8.GetByteCount(window.Payload)), ("$payload", window.Payload),
                    ("$hash", Hash(window.Payload)), ("$positions", JsonSerializer.Serialize(window.LastTicks)));
                foreach (var (tag, ticks) in window.LastTicks)
                {
                    SetSessionPosition(id, tag, "buffered_ticks", ticks);
                    Execute("UPDATE tags SET buffered_ticks=$ticks WHERE owner=$id AND tag_key=$tag",
                        ("$ticks", ticks), ("$id", id), ("$tag", Key(tag)));
                }
            }
            Execute("UPDATE sessions SET next_slot=$next,state=CASE WHEN $next=total_slots THEN 'Draining' ELSE 'Ready' END WHERE id=$id",
                ("$id", id), ("$next", window.NextSlot));
            completed = CompleteIfDrained(id);
            FaultPoint?.Invoke("before_generation_commit");
        });
        FaultPoint?.Invoke("after_generation_commit");
        Log(LogLevel.Debug, "generation.committed", "Generate", "Generated window and checkpoint committed to SQLite.",
            sessionId: id, startSlot: session.NextSlot, endSlot: window.NextSlot,
            pointCount: window.PointCount, byteCount: Encoding.UTF8.GetByteCount(window.Payload));
        LogCompletion(id, "Generate", completed);
        return new(id, window.NextSlot != session.NextSlot, null);
    }, sessionId: id);

    // Called only after the transaction commits: a rolled-back completion must
    // never appear as a successful lifecycle event.
    private void LogCompletion(string id, string operation, bool completed)
    {
        if (completed)
            Log(LogLevel.Information, "session.completed", operation,
                Mode == ExecutionMode.Simulation ? "Session completed; all emitted batches were acknowledged by the simulated transport." : "Publishing finished. Inspect arrival observations and obtain user review; per-point storage is not verified.", sessionId: id);
    }

    private (long Points, long Bytes) QueueUsage()
    {
        using var command = Command(QueueQueries.GlobalUsage);
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private long AvailableDiskBytes()
    {
        var drive = DriveInfo.GetDrives().Where(d => databasePath.StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal))
            .OrderByDescending(d => d.RootDirectory.FullName.Length).FirstOrDefault();
        if (drive is null) throw new RuntimeFailure("runtime.disk_unknown", "Cannot determine free space on the state volume. Check the local filesystem before continuing.");
        return drive.AvailableFreeSpace;
    }

    private bool CompleteIfDrained(string id)
    {
        using var command = Command("""
            UPDATE sessions SET state='Complete' WHERE id=$id AND state='Draining'
            AND NOT EXISTS(SELECT 1 FROM batches INDEXED BY batch_outstanding WHERE session_id=$id AND state NOT IN ('Acknowledged','Discarded','Published'))
            """, ("$id", id));
        return command.ExecuteNonQuery() > 0;
    }

    private static List<string> Rotate(List<string> ids, string? after)
    {
        int start = ids.FindIndex(id => string.CompareOrdinal(id, after) > 0);
        if (start <= 0) return ids;
        return [.. ids.Skip(start), .. ids.Take(start)];
    }
}
