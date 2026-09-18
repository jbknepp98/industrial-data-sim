using System.Text;
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
        return ids.Select(Generate).ToArray();
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
            return new(id, false, "Queue limit reached. Drain acknowledged work or increase queue limits; the checkpoint has not advanced.");
        long free = FreeDiskBytes?.Invoke() ?? AvailableDiskBytes();
        if (free < limits.MinimumFreeDiskBytes || free - limits.MinimumFreeDiskBytes < limits.BatchBytes * 4L)
            return new(id, false, "Disk headroom is low. Free space on the state volume before continuing; the checkpoint has not advanced.");
        var model = LoadModel(id);
        var window = GenerationWindow.Generate(model, session.NextSlot, limits.CandidateSlotsPerTurn, limits.BatchPoints, limits.BatchBytes);
        if (window.Error is { } error)
        {
            Execute("UPDATE sessions SET state='Failed',error_code=$code,error_message=$message WHERE id=$id",
                ("$id", id), ("$code", error.Code), ("$message", error.Message));
            return new(id, false, error.Message);
        }
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
                    Execute("UPDATE tags SET buffered_ticks=$ticks WHERE owner=$id AND tag_key=$tag",
                        ("$ticks", ticks), ("$id", id), ("$tag", Key(tag)));
            }
            Execute("UPDATE sessions SET next_slot=$next,state=CASE WHEN $next=total_slots THEN 'Draining' ELSE 'Ready' END WHERE id=$id",
                ("$id", id), ("$next", window.NextSlot));
            CompleteIfDrained(id);
            FaultPoint?.Invoke("before_generation_commit");
        });
        FaultPoint?.Invoke("after_generation_commit");
        return new(id, window.NextSlot != session.NextSlot, null);
    });

    private (long Points, long Bytes) QueueUsage()
    {
        using var command = Command("SELECT COALESCE(SUM(point_count),0),COALESCE(SUM(byte_count),0) FROM batches WHERE state!='Acknowledged'");
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

    private void CompleteIfDrained(string id) => Execute("""
        UPDATE sessions SET state='Complete' WHERE id=$id AND state='Draining'
        AND NOT EXISTS(SELECT 1 FROM batches WHERE session_id=$id AND state!='Acknowledged')
        """, ("$id", id));

    private static List<string> Rotate(List<string> ids, string? after)
    {
        int start = ids.FindIndex(id => string.CompareOrdinal(id, after) > 0);
        if (start <= 0) return ids;
        return [.. ids.Skip(start), .. ids.Take(start)];
    }
}
