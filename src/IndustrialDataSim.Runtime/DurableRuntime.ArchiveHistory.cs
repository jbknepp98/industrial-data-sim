using System.Security.Cryptography;

namespace IndustrialDataSim.Runtime;

public sealed record ArchiveSegmentReceipt(string ArchiveId, string Sha256, long Bytes, long Batches, long ThroughBatch);

public sealed partial class DurableRuntime
{
    /// <summary>Receipts are retained after final archival; pagination never reads payloads.</summary>
    public IReadOnlyList<ArchiveSegmentReceipt> ArchiveSegments(string id, long afterBatch = 0) => Access(() =>
    {
        _ = ReadSession(id);
        if (afterBatch < 0)
            throw new RuntimeFailure("archive.cursor", "Use a nonnegative history cursor; pass the last receipt's ThroughBatch to read the next page.");
        var receipts = new List<ArchiveSegmentReceipt>();
        using var command = Command("SELECT id,sha256,bytes,batches,through_batch FROM archive_segments WHERE session_id=$id AND through_batch>$after ORDER BY through_batch LIMIT 100",
            ("$id", id), ("$after", afterBatch));
        using var row = command.ExecuteReader();
        while (row.Read()) receipts.Add(new(row.GetString(0), row.GetString(1), row.GetInt64(2), row.GetInt64(3), row.GetInt64(4)));
        return receipts;
    }, sessionId: id);

    public void VerifyArchiveSegment(string id, string archiveId, string directory) => Access(() =>
    {
        if (!Guid.TryParseExact(archiveId, "N", out _))
            throw new RuntimeFailure("archive.segment_id", "Use the 32-character archive ID from history receipts. A filename or directory is not an archive ID.");
        using var command = Command("SELECT sha256,bytes FROM archive_segments WHERE session_id=$id AND id=$archive",
            ("$id", id), ("$archive", archiveId));
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new RuntimeFailure("archive.segment_missing", "No segment receipt matches this session and archive ID. List history receipts; an export file alone is not proof of committed pruning.");
        using var file = new FileStream(Path.Combine(directory, archiveId + ".jsonl"), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != row.GetInt64(1) || Convert.ToHexString(SHA256.HashData(file)) != row.GetString(0))
            throw new RuntimeFailure("archive.checksum_mismatch", "History segment differs from its durable receipt. Preserve the file and restore an intact archive copy; never reconstruct history by publishing again.");
        return true;
    }, sessionId: id);
}
