namespace IndustrialDataSim.Runtime;

public sealed record RecoveryStatus(bool IsRecovered, string? RequiredTagPrefix, long QuarantinedSessions, string Message);

public sealed partial class DurableRuntime
{
    // Default-deny all session mutations, including future operations. Recovery
    // preserves original evidence; only these reviewed inspection paths may read it.
    private static readonly HashSet<string> RecoveryReadOperations = new(StringComparer.Ordinal)
    {
        nameof(GetSession), nameof(Batches), nameof(Progress), nameof(Horizon), nameof(HorizonStatus),
        nameof(GetArchive), nameof(VerifyArchive), nameof(ArchiveSegments), nameof(VerifyArchiveSegment), nameof(Observations)
    };

    public RecoveryStatus RecoveryInfo() => Access(() =>
    {
        string? prefix = ReadRecoveryPrefix();
        long count = Convert.ToInt64(Scalar("SELECT COUNT(*) FROM recovery_sessions"));
        return new RecoveryStatus(prefix is not null, prefix, count, prefix is null
            ? "Original runtime state: normal restart rules apply. Preserve Sending/Uncertain work and never replay it blindly."
            : "Recovered evidence: prior sessions are permanently quarantined and excluded from active queue budgets. Start new sessions only under the required tag prefix. Verify no other writer uses the new tags. This recovery does not preserve process continuity on the original tags.");
    });

    // Treat missing or altered recovery metadata as damage, never as permission
    // to fall back to unrestricted admission or print unreviewed metadata.
    private string? ReadRecoveryPrefix()
    {
        string? prefix = Scalar("SELECT value FROM runtime_metadata WHERE key='recovery_namespace'") as string;
        string? sourceHash = Scalar("SELECT value FROM runtime_metadata WHERE key='recovery_source_sha256'") as string;
        string? identityHash = Scalar("SELECT value FROM runtime_metadata WHERE key='recovery_identity_hash'") as string;
        bool hasEvidence = Scalar("SELECT 1 FROM recovery_sessions LIMIT 1") is not null;
        if (prefix is null && sourceHash is null && identityHash is null && !hasEvidence) return null;
        if (prefix is null || prefix.Length != 43 || !prefix.StartsWith("Recovered.", StringComparison.Ordinal) ||
            !prefix.EndsWith(".", StringComparison.Ordinal) || !Guid.TryParseExact(prefix[10..^1], "N", out _) ||
            sourceHash is null || sourceHash.Length != 64 || !sourceHash.All(Uri.IsHexDigit) ||
            identityHash != Hash(prefix + "\n" + sourceHash))
            throw new RuntimeFailure("backup.recovery_integrity", "Recovery identity is missing or damaged. Preserve this database and its trusted source snapshot/receipt. Create a new guarded restore if needed; do not edit metadata to allow old tag names or reactivate sessions.");
        return prefix;
    }

}
