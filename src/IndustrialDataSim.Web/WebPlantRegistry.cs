using IndustrialDataSim.Runtime;
using Npgsql;

namespace IndustrialDataSim.Web;

/// <summary>A plant this web container hosts, and whether hosting is switched on.</summary>
public sealed record HostedPlantEntry(string Simulation, Guid InterviewId, bool HostingEnabled);

/// <summary>Which plants the web container runs. Tests use an in-memory copy.</summary>
public interface IWebPlantRegistry
{
    void Add(string simulation, Guid interviewId);
    void SetHostingEnabled(string simulation, bool enabled);
    IReadOnlyList<HostedPlantEntry> List();
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
            transaction.Commit();
            tableReady = true;
        }
        return connection;
    }

    public void Add(string simulation, Guid interviewId)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand("INSERT INTO flowsim_web_plants (simulation, interview_id) VALUES (@s, @i) ON CONFLICT (simulation) DO NOTHING", connection);
        command.Parameters.AddWithValue("s", simulation);
        command.Parameters.AddWithValue("i", interviewId);
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
        using var command = new NpgsqlCommand("SELECT simulation, interview_id, hosting_enabled FROM flowsim_web_plants ORDER BY simulation", connection);
        using var reader = command.ExecuteReader();
        var entries = new List<HostedPlantEntry>();
        while (reader.Read()) entries.Add(new(reader.GetString(0), reader.GetGuid(1), reader.GetBoolean(2)));
        return entries;
    }
}

/// <summary>An in-process registry for tests.</summary>
public sealed class InMemoryWebPlantRegistry : IWebPlantRegistry
{
    private readonly Dictionary<string, HostedPlantEntry> entries = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public void Add(string simulation, Guid interviewId) { lock (gate) entries.TryAdd(simulation, new(simulation, interviewId, true)); }

    public void SetHostingEnabled(string simulation, bool enabled)
    {
        lock (gate) if (entries.TryGetValue(simulation, out var entry)) entries[simulation] = entry with { HostingEnabled = enabled };
    }

    public void Remove(string simulation) { lock (gate) entries.Remove(simulation); }

    public IReadOnlyList<HostedPlantEntry> List() { lock (gate) return entries.Values.OrderBy(e => e.Simulation, StringComparer.Ordinal).ToList(); }
}
