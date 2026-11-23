using IndustrialDataSim.Runtime;
using Npgsql;

namespace IndustrialDataSim.Interview;

/// <summary>Token counts the provider reported for one model reply.</summary>
public sealed record InterviewUsage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens)
{
    public static readonly InterviewUsage None = new(0, 0, 0, 0);
    public InterviewUsage Add(InterviewUsage other) =>
        new(InputTokens + other.InputTokens, OutputTokens + other.OutputTokens, CacheReadTokens + other.CacheReadTokens, CacheWriteTokens + other.CacheWriteTokens);
}

/// <summary>
/// One stored conversation message. Role "user" and "assistant" rows are
/// replayed to the model exactly as stored; "notice" rows record what the
/// page was told (refusals, stops) and are never sent to the model.
/// </summary>
public sealed record StoredMessage(int Sequence, string Role, string ContentJson, InterviewUsage? Usage);

public static class InterviewStatus
{
    /// <summary>Waiting for the user's next message.</summary>
    public const string Open = "open";
    /// <summary>A turn is in progress. Found on load, it means the process stopped mid-turn: the turn is continued.</summary>
    public const string InTurn = "in_turn";
    /// <summary>The user approved the preview (phase 7 sets this).</summary>
    public const string Approved = "approved";
}

public sealed record InterviewRecord(Guid Id, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, string Status,
    string? NeedsJson, string? DraftModelJson, string? PreviewJson, string? ApprovedSimulation, IReadOnlyList<StoredMessage> Messages);

/// <summary>
/// Interview persistence. History is append-only: messages are only ever
/// inserted, with consecutive sequence numbers, so replaying them sends the
/// model the same bytes every time (prompt caching and thinking signatures
/// survive restarts).
/// </summary>
public interface IInterviewStore
{
    void Create(Guid id, DateTimeOffset createdUtc);
    InterviewRecord? Load(Guid id);
    /// <summary>Appends messages after the last stored one and sets the status, in one transaction.</summary>
    void Append(Guid id, IReadOnlyList<(string Role, string ContentJson, InterviewUsage? Usage)> messages, string status);
    /// <summary>Saves the needs profile, draft and preview (null clears the draft or preview).</summary>
    void SaveState(Guid id, string needsJson, string? draftModelJson, string? previewJson);
    void SetStatus(Guid id, string status);
    /// <summary>
    /// Records that the user approved this interview's preview and the plant was
    /// admitted under <paramref name="simulation"/> (status approved). Returns
    /// false when the interview was already approved; nothing changes then.
    /// </summary>
    bool MarkApproved(Guid id, string simulation);
    /// <summary>Simulations admitted from an approved interview: the plants a web container may stop, start and remove.</summary>
    IReadOnlySet<string> ApprovedSimulations();

    /// <summary>Stores a file the user attached (<see cref="AttachmentReader.Read"/> checked and converted it).</summary>
    void SaveAttachment(StoredAttachment attachment) =>
        throw new RuntimeFailure("interview.attachments_unsupported", "This interview store cannot keep attached files.");
    /// <summary>An attachment of this interview, with its content; null when there is none with this id.</summary>
    StoredAttachment? LoadAttachment(Guid interviewId, Guid attachmentId) => null;
    /// <summary>How many files are attached to the interview so far.</summary>
    int CountAttachments(Guid interviewId) => 0;
}

/// <summary>
/// Interviews in the shared flow_data_sim database, in simulator-owned tables
/// that no simulation prefix can claim (they are created like
/// flowsim_snapshots, on first use, under the admission lock):
/// flowsim_interviews (one row per interview) and flowsim_interview_messages
/// (the append-only history), plus flowsim_interview_attachments (files the
/// user attached; the history refers to them by id). Message content is stored
/// as text, not jsonb, so it is replayed byte for byte.
/// </summary>
public sealed class PostgresInterviewStore : IInterviewStore, IDisposable
{
    private readonly NpgsqlDataSource source;
    private bool tablesReady;

    public PostgresInterviewStore(string connectionString) => source = NpgsqlDataSource.Create(connectionString);

    /// <summary>Reads FLOWSIM_PG_CONNECTION, like the plant writer. It is never logged or echoed.</summary>
    public static PostgresInterviewStore FromEnvironment()
    {
        string? connection = Environment.GetEnvironmentVariable("FLOWSIM_PG_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
            throw new RuntimeFailure("interview.database_unconfigured", "Set FLOWSIM_PG_CONNECTION to the flow_data_sim connection string (for example in the private env file passed with --env-file). Interviews are saved there so they survive restarts.");
        return new PostgresInterviewStore(connection);
    }

    public void Dispose() => source.Dispose();

    private NpgsqlConnection Open()
    {
        NpgsqlConnection connection;
        try { connection = source.OpenConnection(); }
        catch (Exception error) when (error is NpgsqlException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            throw new RuntimeFailure("interview.database_unreachable", "Could not open the flow_data_sim database to save the interview. Check FLOWSIM_PG_CONNECTION and that the Postgres container is running.");
        }
        EnsureTables(connection);
        return connection;
    }

    private void EnsureTables(NpgsqlConnection connection)
    {
        if (tablesReady) return;
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "SELECT pg_advisory_xact_lock(hashtext('flowsim-admission'))");
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS flowsim_interviews (
              id                  uuid PRIMARY KEY,
              created_utc         timestamptz NOT NULL,
              updated_utc         timestamptz NOT NULL,
              status              text NOT NULL,
              needs_json          text,
              draft_model_json    text,
              preview_json        text,
              approved_simulation text)
            """);
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS flowsim_interview_attachments (
              id                  uuid PRIMARY KEY,
              interview_id        uuid NOT NULL REFERENCES flowsim_interviews (id) ON DELETE CASCADE,
              created_utc         timestamptz NOT NULL,
              name                text NOT NULL,
              kind                text NOT NULL CHECK (kind IN ('pdf', 'image', 'text')),
              media_type          text NOT NULL,
              size_bytes          bigint NOT NULL,
              data                bytea,
              text_content        text,
              truncated           boolean NOT NULL DEFAULT false)
            """);
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS flowsim_interview_messages (
              interview_id        uuid NOT NULL REFERENCES flowsim_interviews (id) ON DELETE CASCADE,
              seq                 integer NOT NULL,
              role                text NOT NULL CHECK (role IN ('user', 'assistant', 'notice')),
              content_json        text NOT NULL,
              input_tokens        bigint,
              output_tokens       bigint,
              cache_read_tokens   bigint,
              cache_write_tokens  bigint,
              created_utc         timestamptz NOT NULL DEFAULT now(),
              PRIMARY KEY (interview_id, seq))
            """);
        transaction.Commit();
        tablesReady = true;
    }

    public void Create(Guid id, DateTimeOffset createdUtc)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand("INSERT INTO flowsim_interviews (id, created_utc, updated_utc, status) VALUES (@i, @c, @c, @s)", connection);
        command.Parameters.AddWithValue("i", id);
        command.Parameters.AddWithValue("c", createdUtc.ToUniversalTime());
        command.Parameters.AddWithValue("s", InterviewStatus.Open);
        command.ExecuteNonQuery();
    }

    public InterviewRecord? Load(Guid id)
    {
        using var connection = Open();
        InterviewRecord? record;
        using (var command = new NpgsqlCommand("SELECT created_utc, updated_utc, status, needs_json, draft_model_json, preview_json, approved_simulation FROM flowsim_interviews WHERE id = @i", connection))
        {
            command.Parameters.AddWithValue("i", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            record = new InterviewRecord(id, reader.GetFieldValue<DateTimeOffset>(0).ToUniversalTime(), reader.GetFieldValue<DateTimeOffset>(1).ToUniversalTime(),
                reader.GetString(2), Text(3), Text(4), Text(5), Text(6), []);
        }
        var messages = new List<StoredMessage>();
        using (var command = new NpgsqlCommand("SELECT seq, role, content_json, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens FROM flowsim_interview_messages WHERE interview_id = @i ORDER BY seq", connection))
        {
            command.Parameters.AddWithValue("i", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                InterviewUsage? usage = reader.IsDBNull(3) ? null : new(reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6));
                messages.Add(new StoredMessage(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), usage));
            }
        }
        return record with { Messages = messages };
    }

    public void Append(Guid id, IReadOnlyList<(string Role, string ContentJson, InterviewUsage? Usage)> messages, string status)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        // Locking the interview row serializes appenders, so sequence numbers never collide.
        int next;
        using (var command = new NpgsqlCommand("SELECT 1 FROM flowsim_interviews WHERE id = @i FOR UPDATE", connection, transaction))
        {
            command.Parameters.AddWithValue("i", id);
            if (command.ExecuteScalar() is null)
                throw new RuntimeFailure("interview.not_found", "The interview no longer exists in flow_data_sim. Start a new interview.");
        }
        using (var command = new NpgsqlCommand("SELECT COALESCE(MAX(seq), 0) + 1 FROM flowsim_interview_messages WHERE interview_id = @i", connection, transaction))
        {
            command.Parameters.AddWithValue("i", id);
            next = System.Convert.ToInt32(command.ExecuteScalar());
        }
        foreach (var (role, content, usage) in messages)
        {
            using var insert = new NpgsqlCommand("""
                INSERT INTO flowsim_interview_messages (interview_id, seq, role, content_json, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens)
                VALUES (@i, @n, @r, @c, @it, @ot, @cr, @cw)
                """, connection, transaction);
            insert.Parameters.AddWithValue("i", id);
            insert.Parameters.AddWithValue("n", next++);
            insert.Parameters.AddWithValue("r", role);
            insert.Parameters.AddWithValue("c", content);
            insert.Parameters.AddWithValue("it", (object?)usage?.InputTokens ?? DBNull.Value);
            insert.Parameters.AddWithValue("ot", (object?)usage?.OutputTokens ?? DBNull.Value);
            insert.Parameters.AddWithValue("cr", (object?)usage?.CacheReadTokens ?? DBNull.Value);
            insert.Parameters.AddWithValue("cw", (object?)usage?.CacheWriteTokens ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }
        using (var update = new NpgsqlCommand("UPDATE flowsim_interviews SET status = @s, updated_utc = now() WHERE id = @i", connection, transaction))
        {
            update.Parameters.AddWithValue("i", id);
            update.Parameters.AddWithValue("s", status);
            update.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void SaveState(Guid id, string needsJson, string? draftModelJson, string? previewJson)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand("UPDATE flowsim_interviews SET needs_json = @n, draft_model_json = @d, preview_json = @p, updated_utc = now() WHERE id = @i", connection);
        command.Parameters.AddWithValue("i", id);
        command.Parameters.AddWithValue("n", needsJson);
        command.Parameters.AddWithValue("d", (object?)draftModelJson ?? DBNull.Value);
        command.Parameters.AddWithValue("p", (object?)previewJson ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void SetStatus(Guid id, string status)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand("UPDATE flowsim_interviews SET status = @s, updated_utc = now() WHERE id = @i", connection);
        command.Parameters.AddWithValue("i", id);
        command.Parameters.AddWithValue("s", status);
        command.ExecuteNonQuery();
    }

    public bool MarkApproved(Guid id, string simulation)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand("UPDATE flowsim_interviews SET status = @s, approved_simulation = @m, updated_utc = now() WHERE id = @i AND approved_simulation IS NULL", connection);
        command.Parameters.AddWithValue("i", id);
        command.Parameters.AddWithValue("s", InterviewStatus.Approved);
        command.Parameters.AddWithValue("m", simulation);
        return command.ExecuteNonQuery() == 1;
    }

    public IReadOnlySet<string> ApprovedSimulations()
    {
        using var connection = Open();
        using var command = new NpgsqlCommand("SELECT DISTINCT approved_simulation FROM flowsim_interviews WHERE approved_simulation IS NOT NULL", connection);
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    public void SaveAttachment(StoredAttachment a)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand("""
            INSERT INTO flowsim_interview_attachments (id, interview_id, created_utc, name, kind, media_type, size_bytes, data, text_content, truncated)
            VALUES (@i, @v, @c, @n, @k, @m, @s, @d, @t, @x)
            """, connection);
        command.Parameters.AddWithValue("i", a.Id);
        command.Parameters.AddWithValue("v", a.InterviewId);
        command.Parameters.AddWithValue("c", a.CreatedUtc.ToUniversalTime());
        command.Parameters.AddWithValue("n", a.Name);
        command.Parameters.AddWithValue("k", a.Kind);
        command.Parameters.AddWithValue("m", a.MediaType);
        command.Parameters.AddWithValue("s", a.SizeBytes);
        command.Parameters.AddWithValue("d", (object?)a.Data ?? DBNull.Value);
        command.Parameters.AddWithValue("t", (object?)a.Text ?? DBNull.Value);
        command.Parameters.AddWithValue("x", a.Truncated);
        command.ExecuteNonQuery();
    }

    public StoredAttachment? LoadAttachment(Guid interviewId, Guid attachmentId)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand("SELECT created_utc, name, kind, media_type, size_bytes, data, text_content, truncated FROM flowsim_interview_attachments WHERE id = @i AND interview_id = @v", connection);
        command.Parameters.AddWithValue("i", attachmentId);
        command.Parameters.AddWithValue("v", interviewId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new StoredAttachment(attachmentId, interviewId, reader.GetFieldValue<DateTimeOffset>(0).ToUniversalTime(), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<byte[]>(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetBoolean(7));
    }

    public int CountAttachments(Guid interviewId)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand("SELECT count(*) FROM flowsim_interview_attachments WHERE interview_id = @v", connection);
        command.Parameters.AddWithValue("v", interviewId);
        return System.Convert.ToInt32(command.ExecuteScalar());
    }

    private static void Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        using var command = new NpgsqlCommand(sql, connection, transaction);
        command.ExecuteNonQuery();
    }
}

/// <summary>An in-process store for tests and offline tools. Same append-only contract as the Postgres store.</summary>
public sealed class InMemoryInterviewStore : IInterviewStore
{
    private readonly Dictionary<Guid, InterviewRecord> interviews = [];
    private readonly object gate = new();

    public void Create(Guid id, DateTimeOffset createdUtc)
    {
        lock (gate) interviews.Add(id, new InterviewRecord(id, createdUtc, createdUtc, InterviewStatus.Open, null, null, null, null, []));
    }

    public InterviewRecord? Load(Guid id)
    {
        lock (gate) return interviews.TryGetValue(id, out var record) ? record with { Messages = record.Messages.ToList() } : null;
    }

    public void Append(Guid id, IReadOnlyList<(string Role, string ContentJson, InterviewUsage? Usage)> messages, string status)
    {
        lock (gate)
        {
            var record = interviews[id];
            var list = record.Messages.ToList();
            foreach (var (role, content, usage) in messages) list.Add(new StoredMessage(list.Count + 1, role, content, usage));
            interviews[id] = record with { Messages = list, Status = status, UpdatedUtc = DateTimeOffset.UtcNow };
        }
    }

    public void SaveState(Guid id, string needsJson, string? draftModelJson, string? previewJson)
    {
        lock (gate) interviews[id] = interviews[id] with { NeedsJson = needsJson, DraftModelJson = draftModelJson, PreviewJson = previewJson };
    }

    public void SetStatus(Guid id, string status)
    {
        lock (gate) interviews[id] = interviews[id] with { Status = status };
    }

    public bool MarkApproved(Guid id, string simulation)
    {
        lock (gate)
        {
            if (interviews[id].ApprovedSimulation is not null) return false;
            interviews[id] = interviews[id] with { Status = InterviewStatus.Approved, ApprovedSimulation = simulation };
            return true;
        }
    }

    public IReadOnlySet<string> ApprovedSimulations()
    {
        lock (gate) return interviews.Values.Select(i => i.ApprovedSimulation).OfType<string>().ToHashSet(StringComparer.Ordinal);
    }

    private readonly Dictionary<Guid, StoredAttachment> attachments = [];

    public void SaveAttachment(StoredAttachment attachment)
    {
        lock (gate)
        {
            if (!interviews.ContainsKey(attachment.InterviewId))
                throw new RuntimeFailure("interview.not_found", "The interview no longer exists. Start a new interview.");
            attachments.Add(attachment.Id, attachment);
        }
    }

    public StoredAttachment? LoadAttachment(Guid interviewId, Guid attachmentId)
    {
        lock (gate) return attachments.TryGetValue(attachmentId, out var a) && a.InterviewId == interviewId ? a : null;
    }

    public int CountAttachments(Guid interviewId)
    {
        lock (gate) return attachments.Values.Count(a => a.InterviewId == interviewId);
    }
}
