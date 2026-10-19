using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace IndustrialDataSim.Runtime;

public sealed record StateRestoreReceipt(string RecoveryId, string DatabaseFile, string ExecutionMode,
    long QuarantinedSessions, string RequiredTagPrefix, string SourceSha256, string Message);

public static partial class StateBackup
{
    /// <summary>
    /// Restore into a new database for inspection and fresh-namespace operation.
    /// No prior session is made runnable, even if its backup labels work Pending.
    /// Source artifacts are never edited and destination filenames never overwrite.
    /// </summary>
    public static StateRestoreReceipt Restore(string snapshot, string expectedSha256, int sourceSchema, string directory)
        => RestoreCore(snapshot, expectedSha256, sourceSchema, directory, null);

    internal static StateRestoreReceipt RestoreCore(string snapshot, string expectedSha256, int sourceSchema, string directory, Action<string>? fault)
    {
        if (sourceSchema is < 8 or > 10)
            throw new RuntimeFailure("backup.restore_schema", "Restore supports source schemas 8–10. Use the source version from the trusted receipt. Preserve older evidence and use a compatible migration investigation; do not edit its version.");
        var verified = Verify(snapshot, expectedSha256);
        try
        {
            string destination = Path.GetFullPath(directory);
            Directory.CreateDirectory(destination);
            string id = Guid.NewGuid().ToString("N");
            string prefix = "Recovered." + id + ".";
            string fileName = "recovered-" + id + ".sqlite";
            string path = Path.Combine(destination, fileName);
            string partial = path + ".partial";
            // Authenticate the copied bytes, not only the earlier source read.
            // This detects a source changed between verification and copying.
            using (var input = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            Verify(partial, expectedSha256);
            long count;
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = partial, Mode = SqliteOpenMode.ReadWrite, Pooling = false
            }.ToString()))
            {
                db.Open();
                bool segments = Convert.ToInt64(Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='archive_segments'")) == 1;
                bool recovery = Convert.ToInt64(Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='recovery_sessions'")) == 1;
                if (segments != (sourceSchema >= 9) || recovery != (sourceSchema >= 10))
                    throw new RuntimeFailure("backup.restore_schema", "The supplied source schema does not match the snapshot tables. Check the original trusted receipt; do not guess a version or edit the evidence.");
                using (var pragmas = db.CreateCommand())
                {
                    pragmas.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
                    pragmas.ExecuteNonQuery();
                }
                // Schema activation and quarantine are one transaction. Before
                // commit the partial is still guarded evidence; afterward every
                // historical session is frozen and all new work needs fresh tags.
                using (var transaction = db.BeginTransaction())
                {
                    using var command = db.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = (sourceSchema < 9 ? Schema.VersionNine : "") +
                        (sourceSchema < 10 ? Schema.VersionTen : "") + """
                        INSERT OR IGNORE INTO recovery_sessions(session_id,prior_state,prior_error_code,prior_error_message)
                          SELECT id,state,error_code,error_message FROM sessions;
                        UPDATE sessions SET state='Uncertain',error_code='backup.session_quarantined',
                          error_message='Restored evidence cannot establish later delivery history. Do not replay or reactivate this session. Inspect recovery-info and start new work under its fresh tag prefix.';
                        INSERT INTO runtime_metadata(key,value) VALUES('recovery_namespace',$prefix)
                          ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                        INSERT INTO runtime_metadata(key,value) VALUES('recovery_source_sha256',$hash)
                          ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                        INSERT INTO runtime_metadata(key,value) VALUES('recovery_identity_hash',$identity)
                          ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                        PRAGMA user_version=10;
                        """;
                    command.Parameters.AddWithValue("$prefix", prefix);
                    command.Parameters.AddWithValue("$hash", verified.Sha256);
                    command.Parameters.AddWithValue("$identity", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prefix + "\n" + verified.Sha256))));
                    command.ExecuteNonQuery();
                    fault?.Invoke("before_restore_commit");
                    transaction.Commit();
                    fault?.Invoke("after_restore_commit");
                }
                count = Convert.ToInt64(Scalar(db, "SELECT COUNT(*) FROM recovery_sessions"));
            }
            using (var durable = new FileStream(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) durable.Flush(true);
            try { ArchiveStorage.Publish(partial, path); }
            catch (RuntimeFailure)
            {
                throw new RuntimeFailure("backup.restore_durability", "Recovered state could not be published durably. Preserve the source and any destination artifacts, check local storage health and inspect the recovery directory before retrying. Never activate a partial file.");
            }
            fault?.Invoke("after_restore_publish");
            return new(id, fileName, verified.ExecutionMode, count, prefix, verified.Sha256,
                "Recovered into a new database with every prior session quarantined. No data was published. Inspect recovery-info; new work must use its fresh tag prefix. Keep the source snapshot, trusted receipt and referenced archives.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or ArgumentException or NotSupportedException)
        {
            throw new RuntimeFailure("backup.restore_storage", "Recovery could not complete. Check source access, destination permissions and free space for a full database copy. Preserve source evidence and partial output; inspect the destination before retrying. No historical replay is authorized.");
        }
    }
}
