using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Plant;

namespace IndustrialDataSim.Tests;

/// <summary>
/// Batch units (lines with kind "batch"): recipes run as phase sequences with
/// setpoint ramps, failures interrupt a phase that later resumes with its
/// remaining time, orders complete with their last batch, and a batch unit can
/// feed a packaging line that consumes its released batches.
/// </summary>
public class PlantBatchTests
{
    internal static readonly DateTimeOffset Anchor = DateTimeOffset.Parse("2026-11-21T18:00:00Z");
    private static readonly string Bases = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "storylines", "bases");

    internal static readonly string[] BatchBases = ["chemicals-batch", "beverage-syrup-batch", "pharma-batch"];

    internal static JsonObject Json(string baseId, Action<JsonObject>? mutate = null)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(Bases, baseId + ".json")))!.AsObject();
        mutate?.Invoke(json);
        return json;
    }

    internal static PlantModel Model(string baseId, Action<JsonObject>? mutate = null)
    {
        var loaded = PlantModelLoader.Load(Json(baseId, mutate).ToJsonString(), Anchor);
        Assert.True(loaded.IsValid, JsonSerializer.Serialize(loaded.Errors));
        return loaded.Model!;
    }

    private static readonly Dictionary<string, PlantRun> Runs = new();

    /// <summary>Seven days after the anchor (13 days of plant time); cached, the engine is deterministic.</summary>
    private static PlantRun Run(string baseId)
    {
        lock (Runs)
        {
            if (!Runs.TryGetValue(baseId, out var run)) Runs[baseId] = run = PlantEngine.Run(Model(baseId), Anchor.AddDays(7));
            return run;
        }
    }

    private static Dictionary<string, List<Dictionary<string, object?>>> Tables(PlantRun run) => PlantPreview.FinalTables(run);

    private static DateTimeOffset Time(object? value) => (DateTimeOffset)value!;

    [Theory]
    [MemberData(nameof(BaseIds))]
    public void BatchBasesAreValidSmallPlantsWithOneBatchUnit(string baseId)
    {
        var model = Model(baseId);
        Assert.Equal(2, model.Lines.Count);
        Assert.Single(model.Lines, l => l.Kind == "batch" && l.Batch is not null);
        Assert.Single(model.Lines, l => l.Kind == "packaging" && l.Batch is null);
        Assert.Contains(PlantSchema.TablesFor(model), t => t.Purpose == "batchphases");
        Assert.Equal(model.Lines.Any(l => l.Batch?.Feeds is not null), PlantSchema.TablesFor(model).Any(t => t.Purpose == "batchconsumption"));
    }

    public static TheoryData<string> BaseIds() => new(BatchBases);

    [Fact]
    public void BatchModelErrorsNameTheFieldAndTheFix()
    {
        static List<(string Code, string Path)> Errors(Action<JsonObject> mutate) =>
            PlantModelLoader.Load(Json("chemicals-batch", mutate).ToJsonString(), Anchor).Errors.Select(e => (e.Code, e.Path)).ToList();
        JsonObject Reactor(JsonObject j) => j["lines"]![0]!.AsObject();
        JsonObject Phase(JsonObject j) => Reactor(j)["batch"]!["recipes"]![0]!["phases"]![1]!.AsObject();

        Assert.Contains(("plant.recipe_sku", "$.lines[0].batch.recipes[0].sku"), Errors(j => Reactor(j)["batch"]!["recipes"]![0]!["sku"] = "DRUM-A"));
        Assert.Contains(("plant.invalid_number", "$.lines[0].batch.recipes[0].phases[1].minutes.minimum"), Errors(j => Phase(j)["minutes"]!["minimum"] = 0));
        Assert.Contains(("plant.invalid_range", "$.lines[0].batch.recipes[0].phases[1].minutes"), Errors(j => Phase(j)["minutes"]!["minimum"] = 200));
        Assert.Contains(("plant.invalid_number", "$.lines[0].batch.recipes[0].phases[1].setpoints.temperatureC.end"), Errors(j => Phase(j)["setpoints"]!["temperatureC"]!["end"] = 350));
        Assert.Contains(("plant.invalid_number", "$.lines[0].batch.recipes[0].phases[1].setpoints.pressureBar.end"), Errors(j => Phase(j)["setpoints"]!["pressureBar"]!["end"] = 25));
        Assert.Contains(("plant.invalid_number", "$.lines[0].batch.recipes[0].phases[1].setpoints.agitatorRpm.start"), Errors(j => Phase(j)["setpoints"]!["agitatorRpm"]!["start"] = 600));
        Assert.Contains(("plant.invalid_line_kind", "$.lines[0].kind"), Errors(j => Reactor(j)["kind"] = "continuous"));
        Assert.Contains(("plant.batch_required", "$.lines[0].batch"), Errors(j => Reactor(j).Remove("batch")));
        Assert.Contains(("plant.batch_on_packaging_line", "$.lines[0].batch"), Errors(j => Reactor(j).Remove("kind")));
        Assert.Contains(("plant.recipe_sku", "$.lines[0].batch.recipes"), Errors(j => Reactor(j)["batch"]!["recipes"]!.AsArray().RemoveAt(1)));
        Assert.Contains(("plant.invalid_feeds", "$.lines[0].batch.feeds.line"), Errors(j => Reactor(j)["batch"]!["feeds"]!["line"] = "R1"));
        Assert.Contains(("plant.invalid_feeds", "$.lines[0].batch.feeds.consumption[0].intermediate"), Errors(j => Reactor(j)["batch"]!["feeds"]!["consumption"]![0]!["intermediate"] = "DRUM-A"));
        Assert.Contains(("plant.invalid_feeds", "$.lines[0].batch.feeds.consumption[0].sku"), Errors(j => Reactor(j)["batch"]!["feeds"]!["consumption"]![0]!["sku"] = "RES-B"));
        Assert.Contains(("plant.batch_sku_shared", "$.lines[0].skus"), Errors(j =>
        {
            j["lines"]![1]!["skus"]!.AsArray().Add("RES-A");
        }));
        Assert.Contains(("session.unknown_property", "$.lines[0].batch.recipes[0].phases[1].setpoints"), Errors(j => Phase(j)["setpoints"]!["speed"] = 1));
    }

    /// <summary>Each batch's phases follow each other without gaps or overlaps; only Down time stretches a phase, and it is recorded.</summary>
    [Theory]
    [MemberData(nameof(BaseIds))]
    public void PhasesTileEachBatch(string baseId)
    {
        var run = Run(baseId);
        var tables = Tables(run);
        var phases = tables["batchphases"].GroupBy(p => (string)p["batch_id"]!).ToDictionary(g => g.Key, g => g.OrderBy(p => (long)Convert.ToInt64(p["sequence"])).ToList());
        var downtime = tables["downtimeevents"];
        int discharged = 0, interrupted = 0;
        foreach (var batch in tables["batches"])
        {
            var list = phases[(string)batch["batch_id"]!];
            var recipe = run.Model.Lines.SelectMany(l => l.Batch?.Recipes ?? []).Single(r => r.Id == (string)batch["recipe_id"]!);
            Assert.Equal(Time(batch["start_utc"]), Time(list[0]["start_utc"]));
            for (int i = 0; i < list.Count; i++)
            {
                Assert.Equal(recipe.Phases[i].Name, list[i]["phase"]);
                if (i > 0) Assert.Equal(Time(list[i - 1]["end_utc"]), Time(list[i]["start_utc"]));
            }
            if (batch["end_utc"] is null) continue;
            discharged++;
            Assert.Equal(recipe.Phases.Count, list.Count);
            Assert.Equal(Time(batch["end_utc"]), Time(list[^1]["end_utc"]));
            foreach (var phase in list)
            {
                double planned = (double)phase["planned_minutes"]!, actual = (double)phase["actual_minutes"]!;
                if (phase["interrupted"] is false) { Assert.Equal(planned, actual, 2); continue; }
                interrupted++;
                // The extra time is exactly the Down time inside the phase, and each stop has a downtime record.
                var start = Time(phase["start_utc"]);
                var end = Time(phase["end_utc"]);
                var down = run.Segments.Where(s => s.Batch?.BatchId == (string)batch["batch_id"]! && s.Batch.Sequence == Convert.ToInt32(phase["sequence"]) && s.State == "Down").ToList();
                Assert.NotEmpty(down);
                double downMinutes = down.Sum(s => (s.End - s.Start).TotalMinutes);
                Assert.True(Math.Abs(actual - planned - downMinutes) < 0.02, $"{actual} - {planned} vs {downMinutes} minutes Down");
                Assert.All(down, s => Assert.Contains(downtime, d => (string?)d["category"] == "Unplanned" && Time(d["start_utc"]) == s.Start));
                Assert.All(down, s => Assert.True(s.Start >= start && s.End <= end));
            }
        }
        Assert.True(discharged >= 20, $"{discharged} batches");
        if (baseId == "chemicals-batch") Assert.True(interrupted > 0, "the chemicals base should interrupt some phases");
    }

    [Fact]
    public void TemperatureFollowsTheSetpointRampAndAgitatorStopsWhileDown()
    {
        var run = Run("chemicals-batch");
        var signals = new PlantSignals(run);
        var temperature = signals.Tags.Single(t => t.Name == "ChemicalsBatch.R1.Temperature");
        var agitator = signals.Tags.Single(t => t.Name == "ChemicalsBatch.R1.AgitatorSpeed");
        var phaseTag = signals.Tags.Single(t => t.Name == "ChemicalsBatch.R1.Phase");
        var batchTag = signals.Tags.Single(t => t.Name == "ChemicalsBatch.R1.BatchId");
        Assert.DoesNotContain(signals.Tags, t => t.Line == "R1" && t.Measure is "Speed" or "GoodCount" or "RejectCount");
        var recipes = run.Model.Lines[0].Batch!.Recipes;
        int checkedHeat = 0;
        // Heat phases that start on profile (not still warm from the previous batch, which settles first).
        foreach (var segment in run.Segments.Where(s => s.Line == "R1" && s.State == "Running" && s.Batch?.Phase == "Heat" && s.Batch.From == 0 && s.Batch.To > 0.99 && Math.Abs(s.Batch.StartC - 40) < 0.5))
        {
            var middle = segment.Start + (segment.End - segment.Start) / 2;
            var ramp = recipes.Single(r => r.Id == segment.Batch!.Recipe).Phases[1].TemperatureC;
            double expected = ramp.At(0.5);
            Assert.InRange((double)signals.ValueAt(temperature, middle)!, expected - 1, expected + 1); // noise sigma 0.15 °C
            Assert.Equal("Heat", signals.ValueAt(phaseTag, middle));
            Assert.Equal(segment.Batch!.BatchId, signals.ValueAt(batchTag, middle));
            checkedHeat++;
        }
        Assert.True(checkedHeat >= 10, $"{checkedHeat} heat phases");

        var down = run.Segments.First(s => s.Line == "R1" && s.State == "Down" && s.Batch is not null && (s.End - s.Start).TotalMinutes > 60 &&
            recipes.Single(r => r.Id == s.Batch.Recipe).Phases[s.Batch.Sequence - 1].TemperatureC.At(s.Batch.From) > 100);
        double first = (double)signals.ValueAt(temperature, down.Start.AddMinutes(1))!, later = (double)signals.ValueAt(temperature, down.Start.AddMinutes(60))!;
        Assert.True(later < first - 20, $"{first} → {later}: temperature relaxes toward ambient while Down");
        Assert.Equal(0.0, signals.ValueAt(agitator, down.Start.AddMinutes(30)));
        Assert.Equal(down.Batch!.Phase, signals.ValueAt(phaseTag, down.Start.AddMinutes(30)));
        var idle = run.Segments.First(s => s.Line == "R1" && s.State == "Idle" && (s.End - s.Start).TotalMinutes > 5);
        Assert.InRange((double)signals.ValueAt(temperature, idle.Start.AddMinutes(2))!, 19, 21);
        Assert.Equal("", signals.ValueAt(batchTag, idle.Start.AddMinutes(2)));
    }

    /// <summary>A failure stops the phase where it was; after the repair it continues from the same progress for its remaining time.</summary>
    [Fact]
    public void FailureMidPhaseResumesWithTheRemainingTimeAndTheTicketLoopIsUnchanged()
    {
        var run = Run("chemicals-batch");
        var tables = Tables(run);
        var segments = run.Segments.Where(s => s.Line == "R1").ToList();
        int resumed = 0;
        for (int i = 1; i + 1 < segments.Count; i++)
        {
            var down = segments[i];
            if (down.State != "Down" || down.Batch is null) continue;
            var before = segments[i - 1];
            var after = segments[i + 1];
            Assert.Equal("Running", before.State);
            Assert.Equal((down.Batch.BatchId, down.Batch.Sequence), (before.Batch!.BatchId, before.Batch.Sequence));
            Assert.Equal(before.Batch.To, down.Batch.From, 9);
            Assert.Equal("Running", after.State);
            Assert.Equal((down.Batch.BatchId, down.Batch.Sequence, down.Batch.From), (after.Batch!.BatchId, after.Batch.Sequence, after.Batch.From));
            // The failure produced the usual ticket: detected detectionMinutes later, completed when the line resumed.
            var failure = run.Failures.Single(f => f.Line == "R1" && f.FailedUtc == down.Start);
            Assert.Equal(down.End, failure.RepairedUtc);
            var record = tables["downtimeevents"].Single(d => Time(d["start_utc"]) == down.Start && (string?)d["line_id"] == "R1");
            Assert.Equal(down.Start.AddMinutes(run.Model.Maintenance.DetectionMinutes), Time(record["detected_utc"]));
            var ticket = tables["maintenancetickets"].Single(t => Equals(t["downtime_id"], record["downtime_id"]));
            Assert.Equal(down.End, Time(ticket["completed_utc"]));
            resumed++;
        }
        Assert.True(resumed >= 2, $"{resumed} interrupted phases resumed");
    }

    /// <summary>Direct-sale batch orders (pharma) complete with their last batch; the lot waits for its batches' release.</summary>
    [Fact]
    public void OrderCompletesWithItsLastBatchAndBecomesALot()
    {
        var run = Run("pharma-batch");
        var tables = Tables(run);
        var unit = run.Model.Lines.Single(l => l.Batch is not null);
        var batches = tables["batches"].GroupBy(b => (string)b["production_order"]!).ToDictionary(g => g.Key, g => g.ToList());
        int completed = 0;
        foreach (var order in tables["productionorders"].Where(o => (string?)o["line_id"] == unit.Id && (string?)o["status"] == "Completed"))
        {
            var list = batches[(string)order["production_order"]!];
            double quantity = Convert.ToDouble(order["quantity_cases"]);
            Assert.Equal((int)Math.Ceiling(quantity / unit.Batch!.Capacity), list.Count);
            Assert.Equal(quantity, list.Sum(b => (double)b["size"]!), 3);
            Assert.All(list, b => Assert.True((double)b["size"]! <= unit.Batch.Capacity));
            var last = list.Max(b => Time(b["end_utc"]));
            Assert.Equal(last, Time(order["completed_utc"]));
            var lot = tables["lots"].Single(l => Equals(l["production_order"], order["production_order"]));
            Assert.Equal(last, Time(lot["produced_utc"]));
            // With batch release testing the batches are sampled, not the lot: the lot is released with its last batch.
            Assert.DoesNotContain(tables["qualitysamples"], s => Equals(s["lot_id"], lot["lot_id"]));
            Assert.All(list, b => Assert.Single(tables["qualitysamples"], s => Equals(s["batch_id"], b["batch_id"])));
            if (lot["released_utc"] is DateTimeOffset released)
                Assert.Equal(list.Max(b => Time(b["released_utc"])), released);
            else Assert.Contains(list, b => b["released_utc"] is null);
            completed++;
        }
        Assert.True(completed >= 5, $"{completed} batch orders completed");
        Assert.Contains(tables["productionorders"], o => (string?)o["line_id"] == unit.Id && batches[(string)o["production_order"]!].Count > 1);
    }

    /// <summary>
    /// The syrup tank feeds the filler: a filling order starts only with enough
    /// released syrup, consumes it oldest batch first, and the genealogy links the
    /// filler lot to the syrup batches. Without syrup the filler waits, Idle.
    /// </summary>
    [Fact]
    public void FedLineConsumesReleasedBatchesWithGenealogy()
    {
        var run = Run("beverage-syrup-batch");
        var model = run.Model;
        var tables = Tables(run);
        var feeds = model.Lines.Single(l => l.Batch is not null).Batch!.Feeds!;
        var batches = tables["batches"].ToDictionary(b => (string)b["batch_id"]!);
        var consumption = tables["batchconsumption"];
        int checkedOrders = 0;
        foreach (var order in tables["productionorders"].Where(o => (string?)o["line_id"] == feeds.Line && o["started_utc"] is not null))
        {
            var use = feeds.Consumption.Single(c => c.Sku == (string)order["sku"]!);
            var rows = consumption.Where(c => Equals(c["production_order"], order["production_order"])).ToList();
            Assert.Equal(Math.Round(Convert.ToDouble(order["quantity_cases"]) * use.PerCase, 3), rows.Sum(r => (double)r["quantity"]!), 3);
            foreach (var row in rows)
            {
                var batch = batches[(string)row["batch_id"]!];
                Assert.Equal(use.Intermediate, model.Lines.SelectMany(l => l.Batch?.Recipes ?? []).Single(r => r.Id == (string)batch["recipe_id"]!).Sku);
                Assert.True(Time(batch["end_utc"]) <= Time(row["consumed_utc"]), "only released batches are consumed");
                Assert.True(Time(row["consumed_utc"]) <= Time(order["started_utc"]));
            }
            checkedOrders++;
        }
        Assert.True(checkedOrders >= 10, $"{checkedOrders} filling orders");
        // No batch gives more than it held; fully used batches are marked Consumed.
        foreach (var group in consumption.GroupBy(c => (string)c["batch_id"]!))
        {
            double used = group.Sum(c => (double)c["quantity"]!), size = (double)batches[group.Key]["size"]!;
            Assert.True(used <= size + 1e-6);
            if ((string?)batches[group.Key]["status"] == "Consumed") Assert.Equal(size, used, 3);
        }
        // Replenishment orders have no sales order; the filler waited for syrup at least once.
        Assert.Contains(tables["productionorders"], o => o["sales_order"] is null && (string?)o["line_id"] == "MX1");
        Assert.DoesNotContain(tables["salesorders"], o => ((string)o["sku"]!).StartsWith("SYR-", StringComparison.Ordinal));
        Assert.Contains(run.Segments, s => s.Line == feeds.Line && s.State == "Idle" && s.Reason!.StartsWith("Waiting for", StringComparison.Ordinal) && s.Reason.EndsWith("syrup", StringComparison.Ordinal));
        // Genealogy: a filler lot back to its syrup batch.
        var lot = tables["lots"].First(l => (string?)l["line_id"] == feeds.Line);
        Assert.Contains(consumption, c => Equals(c["production_order"], lot["production_order"]));
    }

    [Theory]
    [MemberData(nameof(BaseIds))]
    public void EveryBatchChangeMatchesTheTableSchema(string baseId)
    {
        var run = Run(baseId);
        var purposes = PlantSchema.TablesFor(run.Model).Select(t => t.Purpose).ToHashSet();
        foreach (var change in run.Changes)
        {
            Assert.Contains(change.Table, purposes);
            var columns = PlantSchema.Table(run.Model, change.Table).Columns.ToDictionary(c => c.Name, c => c.Type);
            if (change.Operation == "insert") Assert.Equal(columns.Keys.Order(), change.Columns.Keys.Order());
            foreach (var (name, value) in change.Columns)
                if (value is not null)
                    Assert.True(columns[name] switch
                    {
                        "text" => value is string, "timestamptz" => value is DateTimeOffset, "boolean" => value is bool,
                        "bigint" => value is int or long, "numeric" => value is int or long or double or decimal, _ => false
                    }, $"{change.Table}.{name}");
        }
    }

    [Fact]
    public void ConnectSpecAndPreviewCoverBatches()
    {
        var model = Model("beverage-syrup-batch");
        var spec = JsonSerializer.Serialize(PlantConnectSpec.Json(model));
        foreach (var name in new[] { "CurrentBatch", "BatchHistory", "BatchPhases", "BatchGenealogy", "beveragesyrup_batchconsumption", "beveragesyrup_recipephases" })
            Assert.Contains(name, spec);
        Assert.Contains("`batch_id` → `beveragesyrup_batches`", PlantConnectSpec.Markdown(model));
        var preview = JsonNode.Parse(JsonSerializer.Serialize(PlantPreview.Build(PlantEngine.Run(model, Anchor), 3600)))!;
        var batches = preview["summary"]!["batches"]!;
        Assert.True((int)batches["total"]! > 5);
        Assert.All(batches["recipes"]!.AsArray(), r => Assert.True((double?)r!["meanCycleMinutes"] is null or > 100));
        Assert.Contains(preview["segments"]!.AsArray(), s => (string?)s!["phase"] == "Dissolve");
        // Plants without a batch unit keep their preview shape.
        var plain = JsonNode.Parse(JsonSerializer.Serialize(PlantPreview.Build(PlantEngine.Run(PlantCertificationTests.Model([]), Anchor), 3600)))!;
        Assert.Null(plain["summary"]!["batches"]);
        Assert.DoesNotContain("CurrentBatch", JsonSerializer.Serialize(PlantConnectSpec.Json(PlantCertificationTests.Model([]))));
    }

    [Fact]
    public void PackagingSegmentsCarryNoBatchField()
    {
        // The golden hashes serialize segments; packaging segments must not gain a property.
        var segment = new PlantLineSegment("L1", Anchor, Anchor.AddMinutes(1), "Running", "X", "PO-1", null, null, 1, 0);
        Assert.DoesNotContain("Batch", JsonSerializer.Serialize(segment));
    }
}
