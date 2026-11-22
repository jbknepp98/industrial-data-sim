using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Plant;
using IndustrialDataSim.Runtime;
using Npgsql;

namespace IndustrialDataSim.Tests;

/// <summary>
/// Milestone 2: the relational contract and the Postgres writer. The schema and
/// replay tests always run. Database tests run only when FLOWSIM_TEST_PG holds a
/// writer connection string for a flow_data_sim instance (they create and remove
/// their own uniquely named simulations and touch nothing else).
/// </summary>
public class PlantStoreTests
{
    private static readonly DateTimeOffset Anchor = DateTimeOffset.Parse("2026-11-21T18:00:00Z");
    private static readonly string ExamplePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "plants", "bluebird-bev.json");

    private static (PlantModel Model, string Json) Example(string? simulation = null)
    {
        var node = JsonNode.Parse(File.ReadAllText(ExamplePath))!.AsObject();
        if (simulation is not null) node["simulation"] = simulation;
        string json = node.ToJsonString();
        var loaded = PlantModelLoader.Load(json, Anchor);
        Assert.True(loaded.IsValid, JsonSerializer.Serialize(loaded.Errors));
        return (loaded.Model!, json);
    }

    [Fact]
    public void EveryEngineChangeMatchesTheTableSchema()
    {
        var run = PlantEngine.Run(Example().Model, Anchor.AddHours(24));
        foreach (var change in run.Changes)
        {
            var table = PlantSchema.Table(change.Table);
            var columns = table.Columns.ToDictionary(c => c.Name, c => c.Type);
            if (change.Operation == "insert") Assert.Equal(columns.Keys.OrderBy(k => k), change.Columns.Keys.OrderBy(k => k));
            foreach (var (name, value) in change.Columns)
            {
                Assert.True(columns.ContainsKey(name), $"{change.Table}.{name} is not in the schema");
                if (value is null) continue;
                bool ok = columns[name] switch
                {
                    "text" => value is string,
                    "timestamptz" => value is DateTimeOffset,
                    "boolean" => value is bool,
                    "bigint" => value is int or long,
                    "numeric" => value is int or long or double or decimal,
                    _ => false
                };
                Assert.True(ok, $"{change.Table}.{name} has a {value.GetType().Name}, schema says {columns[name]}");
            }
        }
    }

    [Fact]
    public void ReplayingToALaterTimeNeverRewritesEarlierHistory()
    {
        // The live host replays the engine every cycle; delivery relies on the
        // changes up to any time being identical however far the replay runs.
        var model = Example().Model;
        var shortRun = PlantEngine.Run(model, Anchor.AddHours(-48));
        var longRun = PlantEngine.Run(model, Anchor.AddHours(24));
        var cut = Anchor.AddHours(-48);
        var early = shortRun.Changes.Where(c => c.Utc < cut).ToList();
        Assert.NotEmpty(early);
        Assert.Equal(JsonSerializer.Serialize(early), JsonSerializer.Serialize(longRun.Changes.Take(early.Count)));
        Assert.All(longRun.Changes.Skip(early.Count), c => Assert.True(c.Utc >= cut));
        Assert.Equal(Enumerable.Range(1, longRun.Changes.Count).Select(i => (long)i), longRun.Changes.Select(c => c.Sequence));
    }

    private static string? Connection => Environment.GetEnvironmentVariable("FLOWSIM_TEST_PG");

    private static string Unique() => "PlantTest" + Guid.NewGuid().ToString("N")[..10];

    private static List<Dictionary<string, object?>> ReadTable(string connection, string table, IReadOnlyList<PlantColumn> columns)
    {
        using var db = new NpgsqlConnection(connection);
        db.Open();
        using var command = new NpgsqlCommand($"SELECT {string.Join(", ", columns.Select(c => c.Name))} FROM \"{table}\" ORDER BY 1", db);
        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < columns.Count; i++) row[columns[i].Name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static string Normalize(object? value) => value switch
    {
        null => "null",
        // timestamptz keeps microseconds; .NET keeps 100 ns ticks.
        DateTimeOffset t => Microseconds(t.UtcDateTime).ToString("O"),
        DateTime t => Microseconds(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToString("O"),
        double d => Decimal((decimal)d),
        decimal d => Decimal(d),
        int or long => System.Convert.ToInt64(value).ToString(System.Globalization.CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => value.ToString()!
    };

    private static DateTime Microseconds(DateTime t) => new(t.Ticks - t.Ticks % 10, DateTimeKind.Utc);

    private static string Decimal(decimal value)
    {
        string text = Math.Round(value, 6).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return text.Contains('.') ? text.TrimEnd('0').TrimEnd('.') : text;
    }

    [Fact]
    public void StoredTablesEqualTheEngineHistoryAppliedInAnyNumberOfSteps()
    {
        if (Connection is not { } connection) return;
        string name = Unique();
        var (model, json) = Example(name);
        using var store = new PlantStore(connection);
        try
        {
            store.Admit(model, json);
            Assert.Equal(store.Admit(model, json), store.Registration(name)); // idempotent
            var run = PlantEngine.Run(model, Anchor.AddHours(1));
            // Apply in uneven steps with tiny transactions, then again: same result.
            foreach (var step in new[] { -300, -200, -71, 0 })
                store.ApplyChanges(model, run.Changes, Anchor.AddHours(step), perTransaction: 37);
            Assert.Equal(0, store.ApplyChanges(model, run.Changes, Anchor));
            var expected = PlantPreview.FinalTables(new PlantRun(model, run.StartUtc, Anchor, run.Changes.Where(c => c.Utc <= Anchor).ToList(), [], []));
            foreach (var table in PlantSchema.Tables)
            {
                var stored = ReadTable(connection, model.TablePrefix + table.Purpose, table.Columns);
                var wanted = expected.GetValueOrDefault(table.Purpose, []).OrderBy(r => (string)r[table.Key]!, StringComparer.Ordinal).ToList();
                Assert.Equal(wanted.Count, stored.Count);
                var byKey = stored.ToDictionary(r => (string)r[table.Key]!);
                foreach (var row in wanted)
                    foreach (var column in table.Columns)
                        Assert.Equal(Normalize(row[column.Name]), Normalize(byKey[(string)row[table.Key]!][column.Name]));
            }
        }
        finally { try { store.Remove(name); } catch (RuntimeFailure) { } }
    }

    [Fact]
    public void OwnershipIsEnforcedAndRemovalTouchesOnlyTheSimulationsOwnTables()
    {
        if (Connection is not { } connection) return;
        string name = Unique(), stray = Unique();
        var (model, json) = Example(name);
        var (strayModel, strayJson) = Example(stray);
        using var store = new PlantStore(connection);
        using var db = new NpgsqlConnection(connection);
        db.Open();
        // A table someone else made with a would-be prefix blocks admission.
        using (var create = new NpgsqlCommand($"CREATE TABLE \"{strayModel.TablePrefix}handmade\" (id int)", db)) create.ExecuteNonQuery();
        try
        {
            Assert.Equal("plant.prefix_in_use", Assert.Throws<RuntimeFailure>(() => store.Admit(strayModel, strayJson)).Error.Code);
            store.Admit(model, json);
            var changed = JsonNode.Parse(json)!.AsObject();
            changed["seed"] = 1;
            var other = PlantModelLoader.Load(changed.ToJsonString(), Anchor).Model!;
            Assert.Equal("plant.already_admitted", Assert.Throws<RuntimeFailure>(() => store.Admit(other, changed.ToJsonString())).Error.Code);
            using (var second = store.LockSimulation(name))
                Assert.Equal("plant.host_running", Assert.Throws<RuntimeFailure>(() => store.LockSimulation(name)).Error.Code);
            store.Remove(name);
            using var check = new NpgsqlCommand($"SELECT count(*) FROM pg_tables WHERE tablename LIKE '{strayModel.TablePrefix}%' ", db);
            Assert.Equal(1L, check.ExecuteScalar()); // the handmade table is untouched
            Assert.Null(store.Registration(name));
        }
        finally
        {
            using var drop = new NpgsqlCommand($"DROP TABLE IF EXISTS \"{strayModel.TablePrefix}handmade\"", db);
            drop.ExecuteNonQuery();
            try { store.Remove(name); } catch (RuntimeFailure) { }
        }
    }

    private static void AssertTablesMatchEngine(string connection, PlantModel model, DateTimeOffset through)
    {
        var run = PlantEngine.Run(model, through.AddSeconds(1));
        var expected = PlantPreview.FinalTables(new PlantRun(model, run.StartUtc, through, run.Changes.Where(c => c.Utc <= through).ToList(), [], []));
        foreach (var table in PlantSchema.TablesFor(model))
        {
            var stored = ReadTable(connection, model.TablePrefix + table.Purpose, table.Columns);
            var wanted = expected.GetValueOrDefault(table.Purpose, []);
            Assert.Equal(wanted.Count, stored.Count);
            var byKey = stored.ToDictionary(r => (string)r[table.Key]!);
            foreach (var row in wanted)
                foreach (var column in table.Columns)
                    Assert.Equal(Normalize(row[column.Name]), Normalize(byKey[(string)row[table.Key]!][column.Name]));
        }
    }

    /// <summary>
    /// The host catches up in bounded slices, saves snapshots as it goes, resumes
    /// from the stored snapshot after a restart (or a sleep: the clock jumps),
    /// and falls back to a full replay when the snapshot is damaged. The rows are
    /// the engine's full history every time.
    /// </summary>
    [Fact]
    public async Task HostResumesFromSnapshotsAndCatchesUpInSlicesWithTheSameHistory()
    {
        if (Connection is not { } connection) return;
        string name = Unique();
        var (model, json) = Example(name);
        using var store = new PlantStore(connection);
        try
        {
            store.Admit(model, json);
            var clock = Anchor;
            var notices = new List<string>();
            PlantHost Host() => new(store, model, null, "unused") { UtcNow = () => clock, Notice = (e, _) => notices.Add(e) };

            // A 14-day backfill: several bounded cycles, each saving a newer snapshot.
            var host = Host();
            var cycles = new List<PlantCycle>();
            do cycles.Add(await host.CycleAsync(50, CancellationToken.None)); while (!cycles[^1].CaughtUp);
            Assert.True(cycles.Count >= 14 * 24 / PlantHost.BackfillSliceHours, $"{cycles.Count} cycles");
            Assert.All(cycles.Zip(cycles.Skip(1)), p => Assert.True(p.Second.ThroughUtc - p.First.ThroughUtc <= TimeSpan.FromHours(PlantHost.BackfillSliceHours)));
            Assert.Equal(Anchor, cycles[^1].ThroughUtc);
            Assert.Equal(new DateTimeOffset(Anchor.UtcDateTime.Date.AddHours(Anchor.Hour), TimeSpan.Zero), store.SnapshotUtc(name));
            Assert.Empty(notices);
            AssertTablesMatchEngine(connection, model, Anchor);

            // Restart after the laptop slept for 9 hours: resume from the snapshot, catch up.
            clock = Anchor.AddHours(9).AddMinutes(13);
            host = Host();
            do cycles.Add(await host.CycleAsync(50, CancellationToken.None)); while (!cycles[^1].CaughtUp);
            Assert.Equal(["plant.snapshot_resumed"], notices);
            Assert.Equal(Anchor.AddHours(9), store.SnapshotUtc(name));
            AssertTablesMatchEngine(connection, model, clock);

            // A damaged snapshot is ignored: full replay, same history, and a fresh snapshot.
            using (var db = new NpgsqlConnection(connection))
            {
                db.Open();
                using var damage = new NpgsqlCommand("UPDATE flowsim_snapshots SET state = state || ' ' WHERE simulation = @s", db);
                damage.Parameters.AddWithValue("s", name);
                Assert.Equal(1, damage.ExecuteNonQuery());
            }
            notices.Clear();
            clock = clock.AddHours(2);
            host = Host();
            do cycles.Add(await host.CycleAsync(50, CancellationToken.None)); while (!cycles[^1].CaughtUp);
            Assert.Equal(["plant.snapshot_ignored"], notices);
            AssertTablesMatchEngine(connection, model, clock);
            Assert.Equal(Anchor.AddHours(11), store.SnapshotUtc(name));
            Assert.NotNull(store.LoadSnapshot(store.Registration(name)!, null).Snapshot);
        }
        finally { try { store.Remove(name); } catch (RuntimeFailure) { } }
        Assert.Null(store.SnapshotUtc(name)); // removal deletes the snapshot with the registration
    }

    /// <summary>
    /// A certification plant: its four extra tables are created, hold exactly the
    /// engine's rows (including the planted uncertified closure), and removal
    /// drops them with the rest.
    /// </summary>
    [Fact]
    public void CertificationTablesAreCreatedFilledAndRemoved()
    {
        if (Connection is not { } connection) return;
        string name = Unique();
        var json = PlantCertificationTests.Json(["uncertified-technician-closure", "uncertified-operator-run"], j => j["simulation"] = name);
        var model = PlantModelLoader.Load(json.ToJsonString(), Anchor).Model!;
        using var store = new PlantStore(connection);
        using var db = new NpgsqlConnection(connection);
        db.Open();
        long Tables()
        {
            using var count = new NpgsqlCommand($"SELECT count(*) FROM pg_tables WHERE tablename IN ('{model.TablePrefix}trainingrecords', '{model.TablePrefix}workcompliance', '{model.TablePrefix}workinstructions', '{model.TablePrefix}operatorcompliance')", db);
            return (long)count.ExecuteScalar()!;
        }
        try
        {
            store.Admit(model, json.ToJsonString());
            Assert.Equal(4L, Tables());
            var run = PlantEngine.Run(model, Anchor.AddHours(1));
            store.ApplyChanges(model, run.Changes, Anchor);
            AssertTablesMatchEngine(connection, model, Anchor);
            using var planted = new NpgsqlCommand($"SELECT count(*) FROM \"{model.TablePrefix}workcompliance\" c JOIN \"{model.TablePrefix}maintenancetickets\" t ON t.ticket_id = c.ticket_id WHERE NOT c.certified AND c.technician_id = 'E1008' AND t.failure_code = 'FL201-VALVE'", db);
            Assert.True((long)planted.ExecuteScalar()! >= 1);
            store.Remove(name);
            Assert.Equal(0L, Tables());
        }
        finally { try { store.Remove(name); } catch (RuntimeFailure) { } }
    }

    /// <summary>
    /// A fed batch plant: recipes, batches, phases and the consumption genealogy
    /// are created, hold exactly the engine's rows, and removal drops them.
    /// </summary>
    [Fact]
    public void BatchTablesAreCreatedFilledAndRemoved()
    {
        if (Connection is not { } connection) return;
        string name = Unique();
        var json = PlantBatchTests.Json("beverage-syrup-batch", j => j["simulation"] = name);
        var model = PlantModelLoader.Load(json.ToJsonString(), Anchor).Model!;
        using var store = new PlantStore(connection);
        using var db = new NpgsqlConnection(connection);
        db.Open();
        string[] purposes = ["recipes", "recipephases", "batches", "batchphases", "batchconsumption"];
        long Tables()
        {
            using var count = new NpgsqlCommand($"SELECT count(*) FROM pg_tables WHERE tablename IN ({string.Join(", ", purposes.Select(p => $"'{model.TablePrefix}{p}'"))})", db);
            return (long)count.ExecuteScalar()!;
        }
        try
        {
            store.Admit(model, json.ToJsonString());
            Assert.Equal(5L, Tables());
            var run = PlantEngine.Run(model, Anchor.AddHours(1));
            store.ApplyChanges(model, run.Changes, Anchor);
            AssertTablesMatchEngine(connection, model, Anchor);
            using var genealogy = new NpgsqlCommand($"SELECT count(*) FROM \"{model.TablePrefix}batchconsumption\" c JOIN \"{model.TablePrefix}batches\" b ON b.batch_id = c.batch_id JOIN \"{model.TablePrefix}lots\" l ON l.production_order = c.production_order WHERE b.end_utc <= c.consumed_utc", db);
            Assert.True((long)genealogy.ExecuteScalar()! >= 1);
            store.Remove(name);
            Assert.Equal(0L, Tables());
        }
        finally { try { store.Remove(name); } catch (RuntimeFailure) { } }
    }

    /// <summary>An interrupted tag batch is sent again: intent cleared, cursor kept, counted.</summary>
    [Fact]
    public void InterruptedTagBatchIsResentNotSkipped()
    {
        if (Connection is not { } connection) return;
        string name = Unique();
        var (model, json) = Example(name);
        using var store = new PlantStore(connection);
        try
        {
            store.Admit(model, json);
            Assert.False(store.ResendInterruptedBatch(name));
            store.BeginSending(name, model.StartUtc);
            store.CompleteSending(name, model.StartUtc);
            store.BeginSending(name, model.StartUtc.AddMinutes(5)); // then the power went out
            Assert.True(store.ResendInterruptedBatch(name));
            var registration = store.Registration(name)!;
            Assert.Equal(model.StartUtc, registration.TagCursorUtc); // the batch after the cursor goes again
            Assert.Null(registration.SendingUntilUtc);
            Assert.Equal(1, registration.UncertainBatches);
            Assert.False(store.ResendInterruptedBatch(name));
        }
        finally { try { store.Remove(name); } catch (RuntimeFailure) { } }
    }
}
