using IndustrialDataSim.Runtime;
using Npgsql;

namespace IndustrialDataSim.Web;

/// <summary>
/// A plant this web container hosts, whether hosting is switched on, and the
/// Historian Dataset its tags go to. DatasetCreated is true only when the page
/// created that Dataset for this plant; only then may the page delete it.
/// Dataset is null for plants approved before plants had their own Dataset;
/// they write to TIMEBASE_DATASET.
/// </summary>
public sealed record HostedPlantEntry(string Simulation, Guid InterviewId, bool HostingEnabled, string? Dataset = null, bool DatasetCreated = false);

/// <summary>Which plants the web container runs. Tests use an in-memory copy.</summary>
public interface IWebPlantRegistry
{
    void Add(string simulation, Guid interviewId, string? dataset = null, bool datasetCreated = false);
    void SetHostingEnabled(string simulation, bool enabled);
    IReadOnlyList<HostedPlantEntry> List();
}

public static class WebPlantRegistryExtensions
{
    /// <summary>The entry for one plant, or null when the page did not approve it.</summary>
    public static HostedPlantEntry? Find(this IWebPlantRegistry registry, string simulation) =>
        registry.List().FirstOrDefault(e => e.Simulation == simulation);
}

/// <summary>
/// flowsim_web_plants in flow_data_sim: one row per plant admitted from the web
/// page, with its hosting switch (Stop/Start on the Plants tab), so a container
/// restart starts exactly the plants that were running. The row references
/// flowsim_simulations and disappears with it when the plant is removed.
/// Like the other simulator-owned tables it is created on first use under the
/// admission lock, and no simulation prefix can claim its name.
/// </summary>
public sealed class PostgresWebPlantRegistry : IWebPlantRegistry, IDisposable
{
    private readonly NpgsqlDataSource source;
    private bool tableReady;

    public PostgresWebPlantRegistry(string connectionString) => source = NpgsqlDataSource.Create(connectionString);

    public void Dispose() => source.Dispose();

    private NpgsqlConnection Open()
    {
        NpgsqlConnection connection;
        try { connection = source.OpenConnection(); }
        catch (Exception error) when (error is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            throw new RuntimeFailure("web.database_unreachable", "Could not open the flow_data_sim database. Check FLOWSIM_PG_CONNECTION and that the Postgres container is running.");
        }
        if (!tableReady)
        {
            using var transaction = connection.BeginTransaction();
            using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('flowsim-admission'))", connection, transaction)) lockCommand.ExecuteNonQuery();
            using (var create = new NpgsqlCommand("""
                CREATE TABLE IF NOT EXISTS flowsim_web_plants (
                  simulation       text PRIMARY KEY REFERENCES flowsim_simulations (simulation) ON DELETE CASCADE,
                  interview_id     uuid NOT NULL,
                  hosting_enabled  boolean NOT NULL DEFAULT true,
                  admitted_utc     timestamptz NOT NULL DEFAULT now())
                """, connection, transaction)) create.ExecuteNonQuery();
            // Added in 2.0.0-rc.2: each plant's own Dataset. Older rows keep NULL
            // and write to TIMEBASE_DATASET, as they always did. ALTER TABLE takes
            // an exclusive lock even when the columns exist, which can deadlock
            // with a concurrent plant removal (its cascade touches this table),
            // so it runs only when a column is actually missing.
            using (var check = new NpgsqlCommand("""
                SELECT count(*) FROM information_schema.columns
                WHERE table_schema = current_schema() AND table_name = 'flowsim_web_plants' AND column_name IN ('dataset', 'dataset_created')
                """, connection, transaction))
            {
                if (Convert.ToInt64(check.ExecuteScalar()) < 2)
                    using (var columns = new NpgsqlCommand("""
                        ALTER TABLE flowsim_web_plants
                          ADD COLUMN IF NOT EXISTS dataset text,
                          ADD COLUMN IF NOT EXISTS dataset_created boolean NOT NULL DEFAULT false
                        """, connection, transaction)) columns.ExecuteNonQuery();
            }
            transaction.Commit();
            tableReady = true;
        }
        return connection;
    }

    public void Add(string simulation, Guid interviewId, string? dataset = null, bool datasetCreated = false)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand("""
            INSERT INTO flowsim_web_plants (simulation, interview_id, dataset, dataset_created) VALUES (@s, @i, @d, @c)
            ON CONFLICT (simulation) DO NOTHING
            """, connection);
        command.Parameters.AddWithValue("s", simulation);
        command.Parameters.AddWithValue("i", interviewId);
        command.Parameters.AddWithValue("d", (object?)dataset ?? DBNull.Value);
        command.Parameters.AddWithValue("c", datasetCreated);
        command.ExecuteNonQuery();
    }

    public void SetHostingEnabled(string simulation, bool enabled)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand("UPDATE flowsim_web_plants SET hosting_enabled = @e WHERE simulation = @s", connection);
        command.Parameters.AddWithValue("s", simulation);
        command.Parameters.AddWithValue("e", enabled);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<HostedPlantEntry> List()
    {
        using var connection = Open();
        using var command = new NpgsqlCommand("SELECT simulation, interview_id, hosting_enabled, dataset, dataset_created FROM flowsim_web_plants ORDER BY simulation", connection);
        using var reader = command.ExecuteReader();
        var entries = new List<HostedPlantEntry>();
        while (reader.Read())
            entries.Add(new(reader.GetString(0), reader.GetGuid(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4)));
        return entries;
    }
}

/// <summary>An in-process registry for tests.</summary>
public sealed class InMemoryWebPlantRegistry : IWebPlantRegistry
{
    private readonly Dictionary<string, HostedPlantEntry> entries = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public void Add(string simulation, Guid interviewId, string? dataset = null, bool datasetCreated = false)
    {
        lock (gate) entries.TryAdd(simulation, new(simulation, interviewId, true, dataset, datasetCreated));
    }

    public void SetHostingEnabled(string simulation, bool enabled)
    {
        lock (gate) if (entries.TryGetValue(simulation, out var entry)) entries[simulation] = entry with { HostingEnabled = enabled };
    }

    public void Remove(string simulation) { lock (gate) entries.Remove(simulation); }

    public IReadOnlyList<HostedPlantEntry> List() { lock (gate) return entries.Values.OrderBy(e => e.Simulation, StringComparer.Ordinal).ToList(); }
}
