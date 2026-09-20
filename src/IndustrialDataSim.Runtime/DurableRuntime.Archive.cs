using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace IndustrialDataSim.Runtime;

public sealed record ArchiveReceipt(string ArchiveId, string Sha256, long Bytes, long Batches);

public sealed partial class DurableRuntime
{
    /// <summary>
    /// Export before pruning. The session tombstone, configuration and tag progress
    /// remain in SQLite forever; archival never permits ID or timestamp reuse.
    /// A crash before the prune transaction leaves the original history intact.
    /// A crash afterward leaves a durable receipt identifying the verified export.
    /// </summary>
    public ArchiveReceipt Archive(string id, string directory) => Access(() =>
    {
        var session = ReadSession(id);
        if (Scalar("SELECT archive_id FROM sessions WHERE id=$id", ("$id", id)) is string)
            throw new RuntimeFailure("archive.already_archived", "This session is already archived. Inspect its archive receipt; do not repeat pruning or recreate its identity.");
        if (session.Status is not (SessionStatus.Complete or SessionStatus.Cancelled) ||
            Scalar("SELECT id FROM batches WHERE session_id=$id AND state NOT IN ('Acknowledged','Discarded','Published') LIMIT 1", ("$id", id)) is not null)
            throw new RuntimeFailure("archive.unresolved", "Only Complete or Cancelled sessions with no outstanding work can be archived. Resolve pending or uncertain delivery first; archival cannot discard it.");
        _ = LoadModel(id);
        try { directory = Path.GetFullPath(directory); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        { throw new RuntimeFailure("archive.invalid_directory", "Supply a valid archive directory on writable local storage; original audit rows are unchanged."); }
        Directory.CreateDirectory(directory);
        string archiveId = Guid.NewGuid().ToString("N");
        string path = Path.Combine(directory, archiveId + ".jsonl");
        long count = 0;
        long bytes = 0;
        // CreateNew prevents replacing an existing archive. Flush(true) and a
        // second read validate the file before the database can lose audit rows.
        using var writtenHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            void Write(object record)
            {
                byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record) + "\n");
                if (bytes + data.Length > 128L * 1024 * 1024)
                    throw new RuntimeFailure("archive.size_limit", "Archive exceeds the 128 MiB operation limit. Original audit rows are unchanged; retain them and use a future streaming partition export. An incomplete export may remain.");
                file.Write(data);
                writtenHash.AppendData(data);
                bytes += data.Length;
            }
            Write(new { kind = "manifest", schemaVersion = 1, archiveId, mode = ModeName, session,
                configuration = (string)Scalar("SELECT config FROM sessions WHERE id=$id", ("$id", id))!,
                configurationHash = (string)Scalar("SELECT config_hash FROM sessions WHERE id=$id", ("$id", id))!,
                generatorVersion = 1, progress = Progress(id),
                preflightSettings = Scalar("SELECT settings FROM production_preflight WHERE session_id=$id", ("$id", id)) as string,
                baseline = Scalar("SELECT baseline FROM production_preflight WHERE session_id=$id", ("$id", id)) as string });
            using var command = Command("""
                SELECT b.id,b.state,b.start_slot,b.end_slot,b.point_count,b.byte_count,b.hash,b.positions,a.state,a.error_code,o.status,o.matching_tags,o.nonnull_current_tags,o.changed_tags,o.error_code,o.user_review
                FROM batches b LEFT JOIN attempts a ON a.batch_id=b.id LEFT JOIN observations o ON o.batch_id=b.id WHERE b.session_id=$id ORDER BY b.id
                """, ("$id", id));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                Write(new { kind = "batch", id = reader.GetInt64(0), state = reader.GetString(1),
                    startSlot = reader.GetInt64(2), endSlot = reader.GetInt64(3), points = reader.GetInt64(4),
                    bytes = reader.GetInt64(5), hash = reader.GetString(6), positions = reader.GetString(7),
                    attemptState = reader.IsDBNull(8) ? null : reader.GetString(8),
                    errorCode = reader.IsDBNull(9) ? null : reader.GetString(9),
                    observation = reader.IsDBNull(10) ? null : new { status = reader.GetString(10), matchingTags = reader.GetInt32(11),
                        nonnullCurrentTags = reader.GetInt32(12), changedTags = reader.GetInt32(13),
                        errorCode = reader.IsDBNull(14) ? null : reader.GetString(14), userReview = reader.GetString(15) } });
                count++;
            }
            Write(new { kind = "complete", batches = count });
            file.Flush(flushToDisk: true);
        }
        string checksum = Convert.ToHexString(writtenHash.GetHashAndReset());
        FaultPoint?.Invoke("before_archive_verify");
        // Hold the export open without write/delete sharing until pruning commits.
        // Keep archives on local durable storage and back them up with the database.
        using (var verified = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (verified.Length != bytes || Convert.ToHexString(SHA256.HashData(verified)) != checksum)
                throw new RuntimeFailure("archive.verification", "Archive verification failed. Original rows remain; check storage health before another export.");
            FaultPoint?.Invoke("after_archive_export");
            InTransaction(() =>
            {
                Execute("INSERT INTO archives(id,session_id,sha256,bytes,batches) VALUES($a,$id,$h,$b,$n)",
                    ("$a", archiveId), ("$id", id), ("$h", checksum), ("$b", bytes), ("$n", count));
                Execute("DELETE FROM observations WHERE batch_id IN (SELECT id FROM batches WHERE session_id=$id)", ("$id", id));
                Execute("DELETE FROM attempts WHERE batch_id IN (SELECT id FROM batches WHERE session_id=$id)", ("$id", id));
                Execute("DELETE FROM batches WHERE session_id=$id", ("$id", id));
                Execute("UPDATE sessions SET archive_id=$a WHERE id=$id", ("$a", archiveId), ("$id", id));
                Execute("UPDATE tags SET owner=NULL WHERE owner=$id", ("$id", id));
                FaultPoint?.Invoke("before_archive_commit");
            });
        }
        FaultPoint?.Invoke("after_archive_commit");
        blockedReasons.Remove(id);
        observationStates.Remove(id);
        Log(LogLevel.Information, "archive.completed", "Archive", "Verified audit export committed; finished batch rows pruned and tag reservations released. Session identity and timestamp limits remain.",
            "Back up the archive with its receipt and the state database. Free SQLite pages are reused; the database file is not automatically compacted.", id);
        return new ArchiveReceipt(archiveId, checksum, bytes, count);
    }, sessionId: id);

    public ArchiveReceipt GetArchive(string id) => Access(() =>
    {
        using var command = Command("SELECT id,sha256,bytes,batches FROM archives WHERE session_id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new RuntimeFailure("archive.missing", "No completed archive receipt exists for this session. Inspect session status; an export file alone does not prove pruning committed.");
        return new ArchiveReceipt(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3));
    }, sessionId: id);

    public void VerifyArchive(string id, string directory) => Access(() =>
    {
        var receipt = GetArchive(id);
        using var file = File.OpenRead(Path.Combine(directory, receipt.ArchiveId + ".jsonl"));
        if (file.Length != receipt.Bytes || Convert.ToHexString(SHA256.HashData(file)) != receipt.Sha256)
            throw new RuntimeFailure("archive.checksum_mismatch", "Archive differs from its durable receipt. Preserve the file and restore a verified backup; do not import or trust its audit history.");
        return true;
    }, sessionId: id);
}
