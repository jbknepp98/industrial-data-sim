using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IndustrialDataSim.Core.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;

namespace IndustrialDataSim.Runtime;

/// <summary>
/// One local database owner, with short serialized transactions. Network work
/// is deliberately outside this class's database lock and transactions.
/// </summary>
public sealed partial class DurableRuntime : IDisposable
{
    private readonly object sync = new();
    private readonly SqliteConnection connection;
    private readonly FileStream owner;
    private readonly string databasePath;
    private readonly RuntimeLimits limits;
    private readonly ILogger<DurableRuntime> logger;
    private SqliteTransaction? transaction;
    private bool disposed;
    private string? generationAfter;
    internal Action<string>? FaultPoint { get; set; }
    internal Func<long>? FreeDiskBytes { get; set; }

    public DurableRuntime(string path, RuntimeLimits? limits = null, ILogger<DurableRuntime>? logger = null)
    {
        this.logger = logger ?? NullLogger<DurableRuntime>.Instance;
        this.limits = limits ?? new();
        this.limits.Validate();
        if (string.IsNullOrWhiteSpace(path))
            throw new RuntimeFailure("runtime.invalid_path", "Supply a state database file path on writable local storage.");
        try { databasePath = Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        { throw new RuntimeFailure("runtime.invalid_path", "State database path is invalid. Supply a valid local file path."); }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            owner = new FileStream(databasePath + ".owner", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new RuntimeFailure("runtime.owner_unavailable",
                "Cannot acquire the database owner file. Stop the other runtime or check directory access; do not delete a live owner file.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new RuntimeFailure("runtime.storage_access", "Cannot access the state directory. Check directory permissions and use writable local storage.");
        }
        connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            Execute("PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint=1000;");
            long version = Convert.ToInt64(Scalar("PRAGMA user_version;"));
            if (version is not (0 or 1 or 2))
                throw new RuntimeFailure("runtime.schema_version", "Unsupported state database version. Open it with the matching simulator version; do not reset or overwrite it.");
            long interrupted = 0;
            InTransaction(() =>
            {
                if (version == 0) Execute(Schema.VersionOne);
                if ((string?)Scalar("SELECT value FROM runtime_metadata WHERE key='execution_mode'") != "simulation-only")
                    throw new RuntimeFailure("runtime.execution_mode", "This database is not marked simulation-only. Use a dedicated simulation state database; fake delivery must not share production checkpoints.");
                if (version < 2) Execute(Schema.VersionTwo);
                interrupted = Convert.ToInt64(Scalar("SELECT COUNT(*) FROM batches WHERE state='Sending'"));
                Execute("""
                    UPDATE sessions SET state='Uncertain',error_code='delivery.interrupted',
                      error_message='Submission was interrupted. Preserve the queued batch and tag ownership; investigate acceptance before resuming. Do not replay.'
                      WHERE id IN (SELECT session_id FROM batches WHERE state='Sending');
                    UPDATE attempts SET state='Uncertain',error_code='delivery.interrupted' WHERE state='Sending';
                    UPDATE batches SET state='Uncertain' WHERE state='Sending';
                    """);
            });
            Log(LogLevel.Information, "runtime.opened", "Open", "Simulation state database opened; startup recovery has committed.");
            if (interrupted > 0)
                Log(LogLevel.Warning, "delivery.interrupted", "Open",
                    $"Startup marked {interrupted} interrupted batches and their sessions Uncertain.",
                    "List Uncertain sessions and inspect their batches. Preserve payloads and tag ownership; investigate acceptance evidence. Do not replay missing samples.");
        }
        catch (Exception error)
        {
            connection.Dispose();
            owner.Dispose();
            if (error is SqliteException or IOException or UnauthorizedAccessException)
                throw StorageFailure();
            throw;
        }
    }

    public void AddSession(string configuration) => Access(() =>
    {
        if (Encoding.UTF8.GetByteCount(configuration) > 1024 * 1024)
            throw new RuntimeFailure("runtime.configuration_limit", "Configuration exceeds 1 MiB. Split it into smaller sessions.");
        var loaded = SimulationDefinitionLoader.Load(configuration);
        if (!loaded.IsValid)
            throw new RuntimeFailure("runtime.invalid_configuration", "Session configuration is invalid. Run validate-simulation on the same configuration and correct its reported fields.");
        var model = loaded.Definition!;
        if (model.Session.OutputTags.Count > 1000)
            throw new RuntimeFailure("runtime.tag_limit", "A durable session supports at most 1000 tags. Split this configuration into sessions with disjoint tags.");
        long total;
        try { total = Core.Simulation.GenerationWindow.TotalSlots(model); }
        catch (OverflowException)
        {
            throw new RuntimeFailure("runtime.slot_limit", "The session sample grid exceeds the supported cursor range. Shorten the range or increase the sampling interval.");
        }
        InTransaction(() =>
        {
            if (Scalar("SELECT id FROM sessions WHERE id=$id", ("$id", model.Session.SessionId)) is not null)
                throw new RuntimeFailure("runtime.session_exists", "This session ID already exists. Inspect or resume it, or use a new ID; configurations are immutable.");
            string profile = Key(model.Session.ConnectionProfile), dataset = Key(model.Session.Dataset);
            foreach (var tag in model.Session.OutputTags)
            {
                using var command = Command("SELECT owner,buffered_ticks FROM tags WHERE profile_key=$p AND dataset_key=$d AND tag_key=$t",
                    ("$p", profile), ("$d", dataset), ("$t", Key(tag.Name)));
                using var reader = command.ExecuteReader();
                if (!reader.Read()) continue;
                if (!reader.IsDBNull(0))
                    throw new RuntimeFailure("runtime.tag_owned", "A requested tag is reserved by another session. Inspect session ownership or choose disjoint tags; paused and uncertain sessions retain ownership.");
                if (!reader.IsDBNull(1) && model.Session.StartUtc.UtcTicks <= reader.GetInt64(1))
                    throw new RuntimeFailure("runtime.backward_range", "The proposed start is not later than this tag's durable history. Choose a later range or a new tag; inserts and equal timestamps are prohibited.");
            }
            Execute("""
                INSERT INTO sessions(id,profile_key,dataset_key,config,config_hash,generator_version,state,next_slot,total_slots)
                VALUES($id,$p,$d,$c,$h,1,'Ready',0,$n)
                """, ("$id", model.Session.SessionId), ("$p", profile), ("$d", dataset),
                ("$c", configuration), ("$h", Hash(configuration)), ("$n", total));
            foreach (var tag in model.Session.OutputTags)
                Execute("""
                    INSERT INTO tags(profile_key,dataset_key,tag_key,tag_name,owner) VALUES($p,$d,$t,$name,$id)
                    ON CONFLICT(profile_key,dataset_key,tag_key) DO UPDATE SET owner=$id,tag_name=$name
                    """, ("$p", profile), ("$d", dataset), ("$t", Key(tag.Name)), ("$name", tag.Name), ("$id", model.Session.SessionId));
            FaultPoint?.Invoke("before_admission_commit");
        });
        Log(LogLevel.Information, "session.admitted", "AddSession", "Session admitted; configuration and tag ownership are durable.", sessionId: model.Session.SessionId);
        return true;
    });

    public SessionSnapshot GetSession(string id) => Access(() => ReadSession(id), sessionId: id);
    public IReadOnlyList<SessionSnapshot> Sessions() => Access(() => SessionIds().Select(ReadSession).ToArray());
    public IReadOnlyList<BatchSnapshot> Batches(string id, long afterId = 0, int limit = 1000) => Access(() =>
    {
        _ = ReadSession(id);
        if (afterId < 0 || limit is < 1 or > 1000)
            throw new RuntimeFailure("runtime.invalid_page", "Use a nonnegative batch cursor and a page size from 1 through 1000.");
        var result = new List<BatchSnapshot>();
        using var command = Command("SELECT id,session_id,state,start_slot,end_slot,point_count,byte_count,hash,payload FROM batches WHERE session_id=$id AND id>$after ORDER BY id LIMIT $limit", ("$id", id), ("$after", afterId), ("$limit", limit));
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(ReadBatch(reader));
        return result;
    }, sessionId: id);

    public IReadOnlyList<TagProgress> Progress(string id) => Access(() =>
    {
        _ = ReadSession(id);
        var result = new List<TagProgress>();
        using var command = Command("SELECT tag_name,buffered_ticks,submitted_ticks,acknowledged_ticks FROM tags WHERE owner=$id ORDER BY tag_key", ("$id", id));
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new(reader.GetString(0), NullableLong(reader, 1), NullableLong(reader, 2), NullableLong(reader, 3)));
        return result;
    }, sessionId: id);

    public void Pause(string id) => Access(() =>
    {
        var state = ReadSession(id).Status;
        if (state is not (SessionStatus.Ready or SessionStatus.Draining or SessionStatus.Paused))
            throw new RuntimeFailure("runtime.cannot_pause", "Only Ready, Draining, or already Paused sessions can be paused. Inspect this session's status first.");
        Execute("UPDATE sessions SET state='Paused' WHERE id=$id", ("$id", id));
        if (state != SessionStatus.Paused)
            Log(LogLevel.Information, "session.paused", "Pause", "Session paused. A submission already in flight may still finish.", sessionId: id);
        return true;
    }, sessionId: id);

    public void Resume(string id) => Access(() =>
    {
        if (ReadSession(id).Status != SessionStatus.Paused)
            throw new RuntimeFailure("runtime.cannot_resume", "Only Paused sessions can resume. Uncertain or failed sessions require investigation; do not reset their checkpoints.");
        bool completed = false;
        InTransaction(() =>
        {
            Execute("UPDATE sessions SET state=CASE WHEN next_slot=total_slots THEN 'Draining' ELSE 'Ready' END WHERE id=$id", ("$id", id));
            completed = CompleteIfDrained(id);
        });
        Log(LogLevel.Information, "session.resumed", "Resume", "Session resumed from its durable checkpoint.", sessionId: id);
        LogCompletion(id, "Resume", completed);
        return true;
    }, sessionId: id);

    public void ReleaseCompleted(string id) => Access(() =>
    {
        if (ReadSession(id).Status != SessionStatus.Complete)
            throw new RuntimeFailure("runtime.cannot_release", "Only completed, fully acknowledged sessions can release tags. Resolve pending or uncertain delivery first.");
        Execute("UPDATE tags SET owner=NULL WHERE owner=$id", ("$id", id));
        blockedReasons.Remove(id);
        Log(LogLevel.Information, "session.tags_released", "ReleaseCompleted", "Completed session tag reservations released; durable tag history remains protected.", sessionId: id);
        return true;
    }, sessionId: id);

    private SessionSnapshot ReadSession(string id)
    {
        using var command = Command(QueueQueries.SessionSnapshot, ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new RuntimeFailure("runtime.session_missing", "Session ID was not found. List sessions and use an existing ID, or admit a new configuration.");
        return new(reader.GetString(0), Enum.Parse<SessionStatus>(reader.GetString(1)), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    private List<string> SessionIds(string? state = null)
    {
        var result = new List<string>();
        using var command = Command("SELECT id FROM sessions WHERE ($state IS NULL OR state=$state) ORDER BY id", ("$state", state));
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private SimulationDefinition LoadModel(string id)
    {
        using var command = Command("SELECT config,config_hash,generator_version FROM sessions WHERE id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new RuntimeFailure("runtime.session_missing", "Session not found. List sessions and use an existing ID.");
        string json = reader.GetString(0);
        if (reader.GetInt32(2) != 1 || Hash(json) != reader.GetString(1))
            throw new RuntimeFailure("runtime.configuration_integrity", "Saved configuration hash or generator version does not match. Stop this session and restore verified state; do not regenerate with an edited definition.");
        var loaded = SimulationDefinitionLoader.Load(json);
        if (!loaded.IsValid) throw new RuntimeFailure("runtime.configuration_integrity", "Saved configuration cannot be loaded. Use a compatible generator version or restore verified state.");
        return loaded.Definition!;
    }

    private T Access<T>(Func<T> action, string? sessionId = null, [CallerMemberName] string operation = "")
    {
        lock (sync)
        {
            if (disposed) throw new RuntimeFailure("runtime.closed", "Runtime is closed. Open the state database before operating on sessions.");
            try { return action(); }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
            {
                var failure = StorageFailure();
                Log(LogLevel.Error, failure.Error.Code, operation, "State storage could not complete the operation.", failure.Error.Message, sessionId);
                failure.Logged = true;
                throw failure;
            }
            catch (RuntimeFailure failure)
            {
                // RuntimeFailure messages are controlled by runtime call sites.
                // Do not forward the exception object or underlying storage errors.
                if (!failure.Logged)
                {
                    Log(LogLevel.Error, failure.Error.Code, operation, "Runtime operation could not complete.", failure.Error.Message, sessionId);
                    failure.Logged = true;
                }
                throw;
            }
        }
    }

    private void InTransaction(Action action)
    {
        using var current = connection.BeginTransaction();
        transaction = current;
        try { action(); current.Commit(); }
        finally { transaction = null; }
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(sql, parameters); command.ExecuteNonQuery(); }
    private object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(sql, parameters); return command.ExecuteScalar(); }
    private static string Key(string value) => value.ToUpperInvariant();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static long? NullableLong(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt64(index);
    private static BatchSnapshot ReadBatch(SqliteDataReader reader) => new(reader.GetInt64(0), reader.GetString(1),
        Enum.Parse<BatchStatus>(reader.GetString(2)), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt32(5),
        reader.GetInt32(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8));
    private static RuntimeFailure StorageFailure() => new("runtime.storage_failure",
        "State storage could not complete the operation. Check disk space, permissions, and database health; reopen and inspect durable session state before retrying.");

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            try { Execute("PRAGMA wal_checkpoint(TRUNCATE);"); }
            catch (SqliteException) { throw StorageFailure(); }
            finally { connection.Dispose(); owner.Dispose(); }
            Log(LogLevel.Information, "runtime.closed", "Dispose", "Simulation state database closed.");
        }
    }
}
