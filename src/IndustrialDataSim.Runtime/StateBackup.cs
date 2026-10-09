using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace IndustrialDataSim.Runtime;

/// <summary>
/// A stopped-owner SQLite snapshot for recovery investigation. It is deliberately
/// not a runnable database: an older snapshot cannot establish which later writes
/// reached Historian. Unsupported schema versions also block older binaries.
/// </summary>
public static class StateBackup
{
    public const int EvidenceSchemaVersion = 1000008;

    public static StateBackupReceipt Create(string database, string directory)
    {
        try
        {
            string source = Path.GetFullPath(database);
            string destination = Path.GetFullPath(directory);
            if (!File.Exists(source))
                throw new RuntimeFailure("backup.source_missing", "State database is missing or inaccessible. Check its location and permissions; backup never creates source state.");
            // Use exactly the runtime's owner mechanism; never remove the owner
            // file. The source is read-only and no migrations or recovery run here.
            using var owner = AcquireOwner(source);
            using var input = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
            input.Open();
            int version = Convert.ToInt32(Scalar(input, "PRAGMA user_version"));
            if (version is < 1 or > 8)
                throw new RuntimeFailure("backup.schema_version", "Source schema is unsupported. Use backup tooling compatible with that runtime; do not reset or edit the database.");
            string? mode = Scalar(input, "SELECT value FROM runtime_metadata WHERE key='execution_mode'") as string;
            if (mode is not ("production" or "simulation-only"))
                throw new RuntimeFailure("backup.execution_mode", "Source execution mode is missing or invalid. Preserve the database and investigate with the matching runtime; do not convert its mode.");
            Directory.CreateDirectory(destination);
            string id = Guid.NewGuid().ToString("D");
            string partial = Path.Combine(destination, id + ".partial");
            string completed = Path.Combine(destination, id + ".evidence.sqlite");
            // Reserve a new file, never overwrite an existing destination. A
            // failed or interrupted partial is not a completed backup receipt.
            using (var reservation = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            using (var output = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = partial, Mode = SqliteOpenMode.ReadWrite, Pooling = false
            }.ToString()))
            {
                output.Open();
                input.BackupDatabase(output);
                using var command = output.CreateCommand();
                command.CommandText = $"PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA user_version={EvidenceSchemaVersion};";
                command.ExecuteNonQuery();
                if ((string?)Scalar(output, "PRAGMA integrity_check") != "ok")
                    throw new RuntimeFailure("backup.integrity", "Snapshot integrity check failed. Preserve the source and partial evidence; check storage health before creating a new backup.");
            }
            using (var durable = new FileStream(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) durable.Flush(true);
            File.Move(partial, completed, overwrite: false);
            using var bytes = File.OpenRead(completed);
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            return new(id, version, mode, bytes.Length, hash,
                "Evidence snapshot created. Keep this receipt separately. This snapshot cannot run or authorize replay; retain the original database for restart.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or ArgumentException or NotSupportedException)
        {
            throw new RuntimeFailure("backup.storage", "Backup could not complete. Check source access, destination permissions and free disk space. Preserve the source; a partial file is not a completed backup. No replay is authorized.");
        }
    }

    private static FileStream AcquireOwner(string source)
    {
        try { return new FileStream(source + ".owner", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            throw new RuntimeFailure("backup.owner_unavailable", "Cannot acquire the database owner. Stop the host and other runtime commands, then retry; check directory access and never delete a live owner file.");
        }
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}

public sealed record StateBackupReceipt(string BackupId, int SourceSchemaVersion,
    string ExecutionMode, long Bytes, string Sha256, string Message);
