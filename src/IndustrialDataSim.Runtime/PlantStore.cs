using System.Security.Cryptography;
using System.Text;
using IndustrialDataSim.Core.Plant;
using Npgsql;

namespace IndustrialDataSim.Runtime;

/// <summary>A plant admitted to flow_data_sim: its stored model, anchor and delivery progress.</summary>
public sealed record PlantRegistration(string Simulation, string TablePrefix, string ModelJson, string ModelSha256,
    DateTimeOffset AnchorUtc, DateTimeOffset StartUtc, long AppliedSequence, DateTimeOffset? TagCursorUtc,
    DateTimeOffset? SendingUntilUtc, long UncertainBatches);

/// <summary>
/// The relational side of a plant in the shared flow_data_sim database.
///
/// Ownership: a simulation creates, writes and drops only tables carrying its
/// own prefix, recorded in flowsim_simulations. Tables with a prefix that no
/// simulation registered are never touched.
///
/// Exactly-once effect: engine changes are applied in sequence order, and the
/// registry's applied_sequence advances in the same transaction as the rows.
/// A crash rolls back both, so a restart applies the same changes again with no
/// duplicates and no gaps. The update of applied_sequence is conditional on the
/// value read, so a second writer cannot interleave silently.
/// </summary>
public sealed class PlantStore : IDisposable
{
    private readonly NpgsqlDataSource source;

    public PlantStore(string connectionString) => source = NpgsqlDataSource.Create(connectionString);

    /// <summary>Reads FLOWSIM_PG_CONNECTION (an Npgsql connection string). It is never logged or echoed.</summary>
    public static PlantStore FromEnvironment()
    {
        string? connection = Environment.GetEnvironmentVariable("FLOWSIM_PG_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
            throw new RuntimeFailure("plant.database_unconfigured", "Set FLOWSIM_PG_CONNECTION to the flow_data_sim connection string (for example in the private env file passed with --env-file).");
        return new PlantStore(connection);
    }

    public void Dispose() => source.Dispose();

    public static string Sha256(string text) => System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>
    /// Registers the plant and creates its tables. Repeating with the same model
    /// and anchor returns the existing registration. A different model under the
    /// same name, a prefix owned by another simulation, or unregistered tables
    /// with this prefix are refused without changing anything.
    /// </summary>
    public PlantRegistration Admit(PlantModel model, string modelJson)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "SELECT pg_advisory_xact_lock(hashtext('flowsim-admission'))");
        string sha = Sha256(modelJson);
        if (Read(connection, transaction, model.Simulation) is { } existing)
        {
            if (existing.ModelSha256 == sha && existing.AnchorUtc == model.AnchorUtc) return existing;
            throw new RuntimeFailure("plant.already_admitted", "A simulation with this name already exists with a different model or anchor. Remove it first (plant remove), or use a new simulation name.");
        }
        using (var owner = new NpgsqlCommand("SELECT simulation FROM flowsim_simulations WHERE table_prefix = @p", connection, transaction))
        {
            owner.Parameters.AddWithValue("p", model.TablePrefix);
            if (owner.ExecuteScalar() is string)
                throw new RuntimeFailure("plant.prefix_owned", "Another simulation owns this table prefix. Choose a different simulation name.");
        }
        using (var stray = new NpgsqlCommand("SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND left(tablename, length(@p)) = @p", connection, transaction))
        {
            stray.Parameters.AddWithValue("p", model.TablePrefix);
            if ((long)stray.ExecuteScalar()! > 0)
                throw new RuntimeFailure("plant.prefix_in_use", "Tables with this prefix already exist but no simulation registered them. They were left untouched; choose a different simulation name.");
        }
        foreach (var table in PlantSchema.TablesFor(model))
        {
            string name = Quote(PlantSchema.PhysicalName(model, table.Purpose));
            var columns = table.Columns.Concat(PlantSchema.StandardColumns)
                .Select(c => $"{Quote(c.Name)} {c.Type}{(c.Nullable ? "" : " NOT NULL")}{(c.Name == "is_deleted" ? " DEFAULT false" : "")}");
            Execute(connection, transaction, $"CREATE TABLE {name} ({string.Join(", ", columns)}, PRIMARY KEY ({Quote(table.Key)}))");
            Execute(connection, transaction, $"CREATE INDEX ON {name} (row_version)");
            Execute(connection, transaction, $"CREATE TRIGGER stamp_row_version BEFORE INSERT OR UPDATE ON {name} FOR EACH ROW EXECUTE FUNCTION flowsim_stamp_row_version()");
            Execute(connection, transaction, $"COMMENT ON TABLE {name} IS '{table.Description.Replace("'", "''")}. Watermark: row_version.'");
        }
        using (var insert = new NpgsqlCommand("INSERT INTO flowsim_simulations (simulation, table_prefix, model_json, model_sha256, anchor_utc, start_utc) VALUES (@s, @p, @m, @h, @a, @t)", connection, transaction))
        {
            insert.Parameters.AddWithValue("s", model.Simulation);
            insert.Parameters.AddWithValue("p", model.TablePrefix);
            insert.Parameters.AddWithValue("m", modelJson);
            insert.Parameters.AddWithValue("h", sha);
            insert.Parameters.AddWithValue("a", model.AnchorUtc);
            insert.Parameters.AddWithValue("t", model.StartUtc);
            insert.ExecuteNonQuery();
        }
        var registration = Read(connection, transaction, model.Simulation)!;
        transaction.Commit();
        return registration;
    }

    public PlantRegistration? Registration(string simulation)
    {
        using var connection = Open();
        return Read(connection, null, simulation);
    }

    public IReadOnlyList<PlantRegistration> List()
    {
        using var connection = Open();
        using var command = new NpgsqlCommand($"{SelectRegistration} ORDER BY simulation", connection);
        using var reader = command.ExecuteReader();
        var all = new List<PlantRegistration>();
        while (reader.Read()) all.Add(Map(reader));
        return all;
    }

    /// <summary>
    /// Applies changes with a sequence above the registration's applied_sequence
    /// and a time at or before throughUtc, in bounded transactions. Returns the
    /// number applied. Rows keep their simulated change time in modified_utc.
    /// </summary>
    public int ApplyChanges(PlantModel model, IReadOnlyList<PlantRecordChange> changes, DateTimeOffset throughUtc, int perTransaction = 500)
    {
        int applied = 0;
        using var connection = Open();
        while (true)
        {
            var registration = Read(connection, null, model.Simulation)
                ?? throw new RuntimeFailure("plant.not_admitted", "This simulation is not registered in flow_data_sim. Run plant build first.");
            var pending = changes.Where(c => c.Sequence > registration.AppliedSequence && c.Utc <= throughUtc)
                .OrderBy(c => c.Sequence).Take(perTransaction).ToList();
            if (pending.Count == 0) return applied;
            // The engine numbers changes 1, 2, 3 … with no gaps; anything else means
            // the stored model no longer reproduces the stored history.
            if (pending[0].Sequence != registration.AppliedSequence + 1)
                throw new RuntimeFailure("plant.history_mismatch", "The plant model no longer reproduces the history already written. Keep the stored model and anchor; never edit them in place.");
            using var transaction = connection.BeginTransaction();
            foreach (var change in pending) Apply(connection, transaction, model, change);
            using (var progress = new NpgsqlCommand("UPDATE flowsim_simulations SET applied_sequence = @n WHERE simulation = @s AND applied_sequence = @o", connection, transaction))
            {
                progress.Parameters.AddWithValue("n", pending[^1].Sequence);
                progress.Parameters.AddWithValue("s", model.Simulation);
                progress.Parameters.AddWithValue("o", registration.AppliedSequence);
                if (progress.ExecuteNonQuery() != 1)
                    throw new RuntimeFailure("plant.concurrent_writer", "Another process advanced this simulation at the same time. Run one plant host per simulation; nothing from this attempt was committed.");
            }
            transaction.Commit();
            applied += pending.Count;
        }
    }

    private static void Apply(NpgsqlConnection connection, NpgsqlTransaction transaction, PlantModel model, PlantRecordChange change)
    {
        var table = PlantSchema.Table(change.Table);
        var types = table.Columns.ToDictionary(c => c.Name, c => c.Type, StringComparer.Ordinal);
        string name = Quote(PlantSchema.PhysicalName(model, table.Purpose));
        using var command = new NpgsqlCommand { Connection = connection, Transaction = transaction };
        int index = 0;
        string Parameter(object? value, string type)
        {
            string p = "p" + index++;
            command.Parameters.AddWithValue(p, ToDatabase(value, type));
            return "@" + p;
        }
        if (change.Operation == "insert")
        {
            var columns = change.Columns.Keys.ToList();
            var values = columns.Select(c => Parameter(change.Columns[c], types[c])).ToList();
            command.CommandText = $"INSERT INTO {name} ({string.Join(", ", columns.Select(Quote))}, modified_utc) VALUES ({string.Join(", ", values)}, {Parameter(change.Utc, "timestamptz")})";
            command.ExecuteNonQuery();
            return;
        }
        var sets = change.Columns.Select(c => $"{Quote(c.Key)} = {Parameter(c.Value, types[c.Key])}").ToList();
        sets.Add($"modified_utc = {Parameter(change.Utc, "timestamptz")}");
        command.CommandText = $"UPDATE {name} SET {string.Join(", ", sets)} WHERE {Quote(table.Key)} = {Parameter(change.Key, "text")}";
        if (command.ExecuteNonQuery() != 1)
            throw new RuntimeFailure("plant.row_missing", "An update found no row to change. The table may have been edited outside the simulator; restore it or remove and rebuild the simulation.");
    }

    private static object ToDatabase(object? value, string type) => value switch
    {
        null => DBNull.Value,
        DateTimeOffset time => time.ToUniversalTime(),
        double number when type == "numeric" => (decimal)Math.Round(number, 6),
        double number => (long)Math.Round(number),
        int whole when type == "numeric" => (decimal)whole,
        int whole => (long)whole,
        long whole when type == "numeric" => (decimal)whole,
        _ => value
    };

    // ---------------- Historian progress ----------------

    /// <summary>Records the intent to publish tag points through untilUtc, before sending them.</summary>
    public void BeginSending(string simulation, DateTimeOffset untilUtc) =>
        Progress(simulation, "UPDATE flowsim_simulations SET sending_until_utc = @u WHERE simulation = @s AND sending_until_utc IS NULL", untilUtc);

    /// <summary>Records that the publish through untilUtc completed.</summary>
    public void CompleteSending(string simulation, DateTimeOffset untilUtc) =>
        Progress(simulation, "UPDATE flowsim_simulations SET tag_cursor_utc = @u, sending_until_utc = NULL WHERE simulation = @s AND sending_until_utc = @u", untilUtc);

    /// <summary>
    /// After a crash between intent and completion, the batch's acceptance is
    /// unknown. Following the simulator's no-replay rule, the cursor moves past
    /// it and the event is counted, instead of sending the points again.
    /// </summary>
    public bool SkipUncertainBatch(string simulation)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand("UPDATE flowsim_simulations SET tag_cursor_utc = sending_until_utc, sending_until_utc = NULL, uncertain_batches = uncertain_batches + 1 WHERE simulation = @s AND sending_until_utc IS NOT NULL", connection);
        command.Parameters.AddWithValue("s", simulation);
        return command.ExecuteNonQuery() == 1;
    }

    private void Progress(string simulation, string sql, DateTimeOffset untilUtc)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("s", simulation);
        command.Parameters.AddWithValue("u", untilUtc.ToUniversalTime());
        if (command.ExecuteNonQuery() != 1)
            throw new RuntimeFailure("plant.progress_conflict", "Tag delivery progress changed unexpectedly. Run one plant host per simulation and inspect plant status.");
    }

    /// <summary>
    /// Holds a session-level advisory lock for one simulation while its host runs,
    /// so two hosts can never deliver the same plant. Dispose releases it.
    /// </summary>
    public IDisposable LockSimulation(string simulation)
    {
        var connection = Open();
        using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext('flowsim-host:' || @s))", connection);
        command.Parameters.AddWithValue("s", simulation);
        if (command.ExecuteScalar() is not true)
        {
            connection.Dispose();
            throw new RuntimeFailure("plant.host_running", "Another plant host is already running this simulation. Stop it first; one host per simulation.");
        }
        return connection; // closing the connection releases the lock
    }

    // ---------------- removal ----------------

    /// <summary>Drops this simulation's tables and registration, and nothing else.</summary>
    public int Remove(string simulation)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var registration = Read(connection, transaction, simulation)
            ?? throw new RuntimeFailure("plant.not_admitted", "No simulation with this name is registered. Nothing was removed.");
        int dropped = 0;
        foreach (var table in PlantSchema.AllTables)
        {
            using (var exists = new NpgsqlCommand("SELECT to_regclass(@t) IS NOT NULL", connection, transaction))
            {
                exists.Parameters.AddWithValue("t", "public." + Quote(registration.TablePrefix + table.Purpose));
                if (exists.ExecuteScalar() is not true) continue;
            }
            Execute(connection, transaction, $"DROP TABLE {Quote(registration.TablePrefix + table.Purpose)}");
            dropped++;
        }
        using (var delete = new NpgsqlCommand("DELETE FROM flowsim_simulations WHERE simulation = @s", connection, transaction))
        {
            delete.Parameters.AddWithValue("s", simulation);
            delete.ExecuteNonQuery();
        }
        transaction.Commit();
        return dropped;
    }

    // ---------------- helpers ----------------

    private const string SelectRegistration = "SELECT simulation, table_prefix, model_json, model_sha256, anchor_utc, start_utc, applied_sequence, tag_cursor_utc, sending_until_utc, uncertain_batches FROM flowsim_simulations";

    private NpgsqlConnection Open()
    {
        try { return source.OpenConnection(); }
        catch (Exception error) when (error is NpgsqlException or TimeoutException)
        {
            throw new RuntimeFailure("plant.database_unavailable", "Could not connect to flow_data_sim. Check that the flow-data-sim-pg container is running, the host and port in FLOWSIM_PG_CONNECTION, and the credentials.");
        }
    }

    private static PlantRegistration? Read(NpgsqlConnection connection, NpgsqlTransaction? transaction, string simulation)
    {
        using var command = new NpgsqlCommand($"{SelectRegistration} WHERE simulation = @s", connection, transaction);
        command.Parameters.AddWithValue("s", simulation);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static PlantRegistration Map(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetInt64(6),
        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
        reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8), reader.GetInt64(9));

    private static void Execute(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        using var command = new NpgsqlCommand(sql, connection, transaction);
        command.ExecuteNonQuery();
    }

    /// <summary>Names come from validated prefixes and fixed purposes; quoting is defense in depth.</summary>
    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
