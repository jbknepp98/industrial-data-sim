using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace IndustrialDataSim.Runtime;

public sealed record ArchiveReceipt(string ArchiveId, string Sha256, long Bytes, long Batches);

public sealed partial class DurableRuntime
{
    private static readonly JsonSerializerOptions ArchiveJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Export before pruning. The session tombstone, configuration and tag progress
    /// remain in SQLite forever; archival never permits ID or timestamp reuse.
    /// A crash before the prune transaction leaves the original history intact.
    /// A crash afterward leaves a durable receipt identifying the verified export.
    /// </summary>
    public ArchiveReceipt Archive(string id, string directory) => ExportArchive(id, directory, null);

    /// <summary>Roll completed audit rows into a bounded segment without changing
    /// model identity, clocks, checkpoints, ownership or the session lifecycle.
    /// Stop all workers before calling; active sessions must first be Paused.</summary>
    public ArchiveReceipt ArchiveHistory(string id, string directory, int maximumBatches = 1000)
    {
        if (maximumBatches is < 1 or > 10000)
            throw new RuntimeFailure("archive.batch_limit", "Choose 1–10000 completed batches per history segment. Use smaller segments if the 128 MiB export limit is reached.");
        return ExportArchive(id, directory, maximumBatches);
    }

    private ArchiveReceipt ExportArchive(string id, string directory, int? maximumBatches) => Access(() =>
    {
        var session = ReadSession(id);
        if (Scalar("SELECT archive_id FROM sessions WHERE id=$id", ("$id", id)) is string)
            throw new RuntimeFailure("archive.already_archived", "This session is already archived. Inspect its archive receipt; do not repeat pruning or recreate its identity.");
        bool segment = maximumBatches is not null;
        if (segment)
        {
            if (session.Status is not (SessionStatus.Paused or SessionStatus.Complete or SessionStatus.Cancelled) ||
                Scalar("SELECT id FROM batches WHERE session_id=$id AND state IN ('Sending','Uncertain') LIMIT 1", ("$id", id)) is not null)
                throw new RuntimeFailure("archive.history_state", "Stop the worker and pause the session before archiving history, or use a Complete/Cancelled session. Sending and Uncertain work must remain untouched; inspect delivery status first.");
        }
        else if (session.Status is not (SessionStatus.Complete or SessionStatus.Cancelled) ||
            Scalar("SELECT id FROM batches WHERE session_id=$id AND state NOT IN ('Acknowledged','Discarded','Published') LIMIT 1", ("$id", id)) is not null)
            throw new RuntimeFailure("archive.unresolved", "Only Complete or Cancelled sessions with no outstanding work can be archived. Resolve pending or uncertain delivery first; archival cannot discard it.");
        long throughBatch = segment ? Convert.ToInt64(Scalar("""
            SELECT COALESCE(MAX(id),0) FROM (SELECT id FROM batches WHERE session_id=$id
              AND state IN ('Acknowledged','Discarded','Published') ORDER BY id LIMIT $limit)
            """, ("$id", id), ("$limit", maximumBatches))) : long.MaxValue;
        if (segment && throughBatch == 0)
            throw new RuntimeFailure("archive.no_completed_history", "No completed batches remain to export. Inspect existing segment receipts and pending work; do not submit or discard work merely to create an archive.");
        long free = FreeDiskBytes?.Invoke() ?? AvailableDiskBytes();
        if (free < limits.MinimumFreeDiskBytes || free - limits.MinimumFreeDiskBytes < 256L * 1024 * 1024)
            throw new RuntimeFailure("archive.disk_low", "Archival requires the configured disk floor plus 256 MiB on the state volume for export and transaction headroom. Free unrelated storage first; do not remove unresolved state or unverified archives.");
        var archiveModel = LoadModel(id);
        try { RestoreProcess(id, archiveModel, session.NextSlot); }
        catch (Core.Configuration.ProcessExecutionFailure failure)
        { throw new RuntimeFailure(failure.Error.Code, failure.Error.Message); }
        try { directory = Path.GetFullPath(directory); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        { throw new RuntimeFailure("archive.invalid_directory", "Supply a valid archive directory on writable local storage; original audit rows are unchanged."); }
        Directory.CreateDirectory(directory);
        string archiveId = Guid.NewGuid().ToString("N");
        string path = Path.Combine(directory, archiveId + ".jsonl");
        string temporary = path + ".partial";
        long count = 0;
        long bytes = 0;
        // CreateNew prevents replacing an existing archive. Flush(true) and a
        // second read validate the file before the database can lose audit rows.
        using var writtenHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            void Write(object record)
            {
                byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, ArchiveJson) + "\n");
                if (bytes + data.Length > 128L * 1024 * 1024)
                    throw new RuntimeFailure("archive.size_limit", "Archive exceeds the 128 MiB operation limit. Original audit rows are unchanged; retain them and use archive-history with fewer batches. An incomplete export may remain.");
                file.Write(data);
                writtenHash.AppendData(data);
                bytes += data.Length;
            }
            Write(new { kind = "manifest", schemaVersion = 3, archiveId, historySegment = segment, throughBatch = segment ? (long?)throughBatch : null, mode = ModeName, session,
                configuration = (string)Scalar("SELECT config FROM sessions WHERE id=$id", ("$id", id))!,
                configurationHash = (string)Scalar("SELECT config_hash FROM sessions WHERE id=$id", ("$id", id))!,
                generatorVersion = 1, horizon = HorizonStatus(id), progress = Progress(id),
                preflightSettings = Scalar("SELECT settings FROM production_preflight WHERE session_id=$id", ("$id", id)) as string,
                baseline = Scalar("SELECT baseline FROM production_preflight WHERE session_id=$id", ("$id", id)) as string });
            using (var checkpoint = Command("SELECT next_slot,payload,hash FROM process_checkpoints WHERE session_id=$id", ("$id", id)))
            using (var saved = checkpoint.ExecuteReader())
                if (saved.Read()) Write(new { kind = "processCheckpoint", version = 1, cursor = saved.GetInt64(0), payload = saved.GetString(1), hash = saved.GetString(2) });
            // Revision zero is represented by the admitted configuration. Later
            // revisions stay in SQLite and are also exported before batch pruning.
            using (var revisions = Command("SELECT revision,request_id,previous_end,new_end,previous_total,new_total,cursor,config_hash,prior_state,resulting_state,committed_utc FROM horizon_revisions WHERE session_id=$id ORDER BY revision", ("$id", id)))
            using (var row = revisions.ExecuteReader())
                while (row.Read()) Write(new { kind = "horizonRevision", revision = row.GetInt32(0), requestId = row.GetString(1),
                    previousEndUtc = Utc(row.GetInt64(2)), endUtc = Utc(row.GetInt64(3)), previousTotal = row.GetInt64(4),
                    total = row.GetInt64(5), cursor = row.GetInt64(6), configurationHash = row.GetString(7),
                    priorState = row.GetString(8), resultingState = row.GetString(9), committedUtc = row.GetString(10) });
            // Segment receipts stay durable and are referenced by subsequent exports.
            // A final archive does not replace or absorb the earlier segment files.
            using (var segments = Command("SELECT id,sha256,bytes,batches,through_batch FROM archive_segments WHERE session_id=$id ORDER BY through_batch DESC LIMIT $limit", ("$id", id), ("$limit", segment ? 1 : -1)))
            using (var prior = segments.ExecuteReader())
                while (prior.Read()) Write(new { kind = "priorSegment", archiveId = prior.GetString(0), sha256 = prior.GetString(1),
                    bytes = prior.GetInt64(2), batches = prior.GetInt64(3), throughBatch = prior.GetInt64(4) });
            using var command = Command("""
                SELECT b.id,b.state,b.start_slot,b.end_slot,b.point_count,b.byte_count,b.hash,b.positions,a.state,a.error_code,o.status,o.matching_tags,o.nonnull_current_tags,o.changed_tags,o.error_code,o.user_review,b.horizon_revision
                FROM batches b LEFT JOIN attempts a ON a.batch_id=b.id LEFT JOIN observations o ON o.batch_id=b.id WHERE b.session_id=$id AND b.id<=$through AND b.state IN ('Acknowledged','Discarded','Published') ORDER BY b.id
                """, ("$id", id), ("$through", throughBatch));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                Write(new { kind = "batch", id = reader.GetInt64(0), state = reader.GetString(1), horizonRevision = reader.GetInt32(16),
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
        ArchiveStorage.Publish(temporary, path);
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
                if (segment)
                    Execute("INSERT INTO archive_segments(id,session_id,sha256,bytes,batches,through_batch) VALUES($a,$id,$h,$b,$n,$through)",
                        ("$a", archiveId), ("$id", id), ("$h", checksum), ("$b", bytes), ("$n", count), ("$through", throughBatch));
                else
                    Execute("INSERT INTO archives(id,session_id,sha256,bytes,batches) VALUES($a,$id,$h,$b,$n)",
                        ("$a", archiveId), ("$id", id), ("$h", checksum), ("$b", bytes), ("$n", count));
                const string selected = "SELECT id FROM batches WHERE session_id=$id AND id<=$through AND state IN ('Acknowledged','Discarded','Published')";
                Execute("DELETE FROM observations WHERE batch_id IN (" + selected + ")", ("$id", id), ("$through", throughBatch));
                Execute("DELETE FROM attempts WHERE batch_id IN (" + selected + ")", ("$id", id), ("$through", throughBatch));
                Execute("DELETE FROM batches WHERE id IN (" + selected + ")", ("$id", id), ("$through", throughBatch));
                if (!segment)
                {
                    Execute("UPDATE sessions SET archive_id=$a WHERE id=$id", ("$a", archiveId), ("$id", id));
                    Execute("UPDATE tags SET owner=NULL WHERE owner=$id", ("$id", id));
                }
                FaultPoint?.Invoke("before_archive_commit");
            });
        }
        FaultPoint?.Invoke("after_archive_commit");
        if (!segment) { blockedReasons.Remove(id); observationStates.Remove(id); }
        Log(LogLevel.Information, segment ? "archive.history_completed" : "archive.completed", segment ? "ArchiveHistory" : "Archive",
            segment ? "Verified history segment committed; completed batch rows pruned. Session clocks, checkpoints and ownership are unchanged." : "Verified audit export committed; finished batch rows pruned and tag reservations released. Session identity and timestamp limits remain.",
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
