using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Plant;

namespace IndustrialDataSim.Tests;

/// <summary>
/// Golden batch drift and batch quality: inside a drift window phases run longer
/// by the drift's factor, the temperature trails the setpoint, yield falls and
/// deviation scores rise; outside it (or after the cleaning PM) batches stay in
/// the golden band. Batch release testing holds batches, fed products inherit
/// their batches' quality, and under GxP a deviated batch is released only after
/// QA closes its deviation.
/// </summary>
public class PlantGoldenBatchTests
{
    private static readonly DateTimeOffset Anchor = PlantBatchTests.Anchor;

    private static readonly Dictionary<string, PlantRun> Runs = new();

    /// <summary>One day past the anchor; cached, the engine is deterministic.</summary>
    private static PlantRun Run(string storyline)
    {
        lock (Runs)
        {
            if (!Runs.TryGetValue(storyline, out var run)) Runs[storyline] = run = PlantEngine.Run(PlantStorylineTests.Build(storyline), Anchor.AddDays(1));
            return run;
        }
    }

    private static DateTimeOffset Time(object? value) => (DateTimeOffset)value!;

    /// <summary>The drift's strength for a batch starting at this time (0 outside the window).</summary>
    private static double Strength(PlantDrift drift, DateTimeOffset start, DateTimeOffset? ended)
    {
        var end = new[] { drift.UntilUtc, ended }.Where(t => t is not null).Min();
        if (start < drift.FromUtc || (end is { } e && start >= e)) return 0;
        return drift.RampDays <= 0 ? 1 : Math.Min(1, (start - drift.FromUtc).TotalDays / drift.RampDays);
    }

    private static DateTimeOffset? CleaningDone(PlantRun run, PlantDrift drift) =>
        drift.EndsWith is null ? null : PlantPreview.FinalTables(run)["maintenancetickets"]
            .Where(t => Equals(t["failure_code"], drift.EndsWith) && t["completed_utc"] is DateTimeOffset done && done >= drift.FromUtc)
            .Select(t => (DateTimeOffset?)t["completed_utc"]).Min();

    [Fact]
    public void GoldenAndDriftModelErrorsNameTheFieldAndTheFix()
    {
        static List<(string Code, string Path)> Errors(string baseId, Action<JsonObject> mutate) =>
            PlantModelLoader.Load(PlantBatchTests.Json(baseId, mutate).ToJsonString(), Anchor).Errors.Select(e => (e.Code, e.Path)).ToList();
        JsonObject Drift(JsonObject j, string line = "R1") => (JsonObject)(j["drifts"] = new JsonArray(new JsonObject
        {
            ["line"] = line, ["phase"] = "Heat", ["cause"] = "fouling", ["from"] = "-3d", ["until"] = "-1d", ["effect"] = new JsonObject { ["phaseMinutesFactor"] = 1.3 }
        }))[0]!;

        Assert.Contains(("plant.invalid_drift", "$.drifts[0].line"), Errors("chemicals-batch", j => Drift(j, "D1")));
        Assert.Contains(("plant.invalid_drift", "$.drifts[0].phase"), Errors("chemicals-batch", j => Drift(j)["phase"] = "Boil"));
        Assert.Contains(("plant.invalid_drift", "$.drifts[0].recipe"), Errors("chemicals-batch", j => Drift(j)["recipe"] = "RCP-NONE"));
        Assert.Contains(("plant.invalid_drift", "$.drifts[0].endsWith"), Errors("chemicals-batch", j => Drift(j)["endsWith"] = "PM-D1-WEEKLY"));
        Assert.Contains(("plant.invalid_drift_time", "$.drifts[0]"), Errors("chemicals-batch", j => Drift(j).Remove("until")));
        Assert.Contains(("plant.invalid_drift_time", "$.drifts[0].until"), Errors("chemicals-batch", j => Drift(j)["until"] = "-4d"));
        Assert.Contains(("plant.invalid_drift", "$.drifts[0].effect"), Errors("chemicals-batch", j => Drift(j)["effect"] = new JsonObject { ["phaseMinutesFactor"] = 1 }));
        Assert.Contains(("plant.invalid_golden", "$.lines[0].batch.recipes[0].golden.phaseMinutes.Heat"),
            Errors("chemicals-batch", j => j["lines"]![0]!["batch"]!["recipes"]![0]!["golden"] = new JsonObject { ["phaseMinutes"] = new JsonObject { ["Heat"] = 200 } }));
        Assert.Contains(("plant.unknown_phase", "$.lines[0].batch.recipes[0].golden.phaseMinutes.Boil"),
            Errors("chemicals-batch", j => j["lines"]![0]!["batch"]!["recipes"]![0]!["golden"] = new JsonObject { ["phaseMinutes"] = new JsonObject { ["Boil"] = 10 } }));
        Assert.Contains(("plant.qa_required", "$.quality.deviations"),
            Errors("pharma-batch", j => j["quality"]!["deviations"] = new JsonObject { ["closeHours"] = new JsonObject { ["minimum"] = 1, ["maximum"] = 2 } }));
        Assert.Contains(("plant.batch_release_required", "$.quality.deviations"), Errors("pharma-batch", j =>
        {
            j["quality"]!.AsObject().Remove("batchRelease");
            j["quality"]!["deviations"] = new JsonObject { ["closeHours"] = new JsonObject { ["minimum"] = 1, ["maximum"] = 2 } };
            j["people"]!["qa"] = new JsonArray(new JsonObject { ["id"] = "E3001", ["name"] = "QA One", ["shift"] = "CrewA" });
        }));
        Assert.Contains(("plant.unused_qa", "$.people.qa"), Errors("pharma-batch", j => j["people"]!["qa"] = new JsonArray(new JsonObject { ["id"] = "E3001", ["name"] = "QA One", ["shift"] = "CrewA" })));
        Assert.Contains(("plant.unknown_parameter", "$.quality.batchRelease.parameters[0].name"), Errors("pharma-batch", j => j["quality"]!["batchRelease"]!["parameters"]![0]!["name"] = "Color"));
        Assert.Contains(("plant.invalid_spec", "$.quality.batchRelease.parameters[0]"), Errors("pharma-batch", j => j["quality"]!["batchRelease"]!["parameters"]![0]!["lower"] = 101));
        // Batch release on a plant without a batch unit.
        var noBatch = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "storylines", "bases", "beverage-2-line.json")))!.AsObject();
        noBatch["quality"]!["batchRelease"] = new JsonObject { ["labMinutes"] = new JsonObject { ["minimum"] = 10, ["maximum"] = 20 }, ["parameters"] = new JsonArray(new JsonObject { ["name"] = "pH" }) };
        Assert.Contains(PlantModelLoader.Load(noBatch.ToJsonString(), Anchor).Errors, e => e.Code == "plant.no_batch_line" && e.Path == "$.quality.batchRelease");
    }

    [Fact]
    public void GoldenProfileDefaultsToTheMiddleOfEachRangeAndIsWrittenAsReferenceData()
    {
        var model = PlantBatchTests.Model("chemicals-batch");
        var recipe = model.Lines[0].Batch!.Recipes[0];
        Assert.Equal(recipe.Phases.Select(p => (p.Minutes.Minimum + p.Minutes.Maximum) / 2), recipe.Golden.PhaseMinutes);
        Assert.Equal(94.0, recipe.Golden.YieldTarget);
        Assert.Equal(1.0, model.Lines[0].Batch!.GoldenThreshold);
        var tables = PlantPreview.FinalTables(PlantEngine.Run(model, model.StartUtc.AddHours(1)));
        var profiles = tables["goldenprofiles"].Where(p => Equals(p["recipe_id"], recipe.Id)).OrderBy(p => Convert.ToInt32(p["sequence"])).ToList();
        Assert.Equal(recipe.Phases.Select(p => p.Name), profiles.Select(p => (string)p["phase"]!));
        Assert.Equal(75.0, profiles.Single(p => Equals(p["phase"], "Heat"))["golden_minutes"]);
        Assert.Contains("\"temperatureC\"", (string)profiles[1]["setpoints_json"]!);
    }

    /// <summary>
    /// 5a: inside the window the heat phase stretches by exactly the drift's
    /// factor at each batch's strength, the temperature lags, yield falls and the
    /// deviation score rises; before the drift and after the cleaning PM every
    /// batch is golden.
    /// </summary>
    [Fact]
    public void ReactorDriftStretchesHeatLowersYieldAndEndsWithTheCleaningPm()
    {
        var run = Run("golden-batch-reactor-drift");
        var drift = run.Model.Drifts.Single();
        var cleaned = CleaningDone(run, drift);
        Assert.NotNull(cleaned);
        Assert.InRange(cleaned!.Value, Anchor.AddDays(-3), Anchor.AddDays(-1)); // about two days before the anchor
        var tables = PlantPreview.FinalTables(run);
        var phases = tables["batchphases"].ToLookup(p => (string)p["batch_id"]!);
        var recipes = run.Model.Lines[0].Batch!.Recipes.ToDictionary(r => r.Id);
        var discharged = tables["batches"].Where(b => b["end_utc"] is DateTimeOffset).ToList();
        int full = 0;
        var inside = new List<Dictionary<string, object?>>();
        var outside = new List<Dictionary<string, object?>>();
        foreach (var batch in discharged)
        {
            double k = Strength(drift, Time(batch["start_utc"]), cleaned);
            var recipe = recipes[(string)batch["recipe_id"]!];
            double factor = 1 + (drift.PhaseMinutesFactor - 1) * k;
            foreach (var phase in phases[(string)batch["batch_id"]!])
            {
                var range = recipe.Phases.Single(p => p.Name == (string)phase["phase"]!).Minutes;
                double f = Equals(phase["phase"], "Heat") ? factor : 1, minutes = (double)phase["planned_minutes"]!;
                Assert.InRange(minutes, Math.Max(1, range.Minimum * f) - 0.02, range.Maximum * f + 0.02);
            }
            (k > 0 ? inside : outside).Add(batch);
            if (k >= 1) full++;
        }
        Assert.True(full >= 8, $"{full} batches at full drift strength");
        // Outside the window (before the drift and after the clean) every batch is in the golden band.
        Assert.True(outside.Count >= 15);
        Assert.All(outside, b => Assert.True(b["golden"] is true, $"{b["batch_id"]} deviation {b["deviation_score"]}"));
        Assert.Contains(outside, b => Time(b["start_utc"]) > cleaned.Value);
        // Inside it: scores rise, most batches leave the band, yield falls about 4 points at full strength.
        double Mean(IEnumerable<Dictionary<string, object?>> rows, string column) => rows.Average(r => (double)r[column]!);
        var strong = inside.Where(b => Strength(drift, Time(b["start_utc"]), cleaned) >= 1).ToList();
        Assert.True(Mean(strong, "deviation_score") > 1.3, $"mean deviation {Mean(strong, "deviation_score")}");
        Assert.True(strong.Count(b => b["golden"] is false) >= strong.Count * 0.7);
        Assert.InRange(Mean(outside, "yield_percent") - Mean(strong, "yield_percent"), 3, 5);
        Assert.True(Mean(outside, "deviation_score") < 1);
    }

    /// <summary>5a in the time series: mid-Heat the temperature trails the setpoint by the drift lag, HeatRate drops, and BatchDeviation shows each batch's score.</summary>
    [Fact]
    public void ReactorDriftShowsInTemperatureHeatRateAndBatchDeviationTags()
    {
        var run = Run("golden-batch-reactor-drift");
        var drift = run.Model.Drifts.Single();
        var cleaned = CleaningDone(run, drift);
        var signals = new PlantSignals(run);
        var temperature = signals.Tags.Single(t => t.Name == "ChemicalsBatch.R1.Temperature");
        var heatRate = signals.Tags.Single(t => t.Name == "ChemicalsBatch.R1.HeatRate");
        var deviation = signals.Tags.Single(t => t.Name == "ChemicalsBatch.R1.BatchDeviation");
        var recipes = run.Model.Lines[0].Batch!.Recipes.ToDictionary(r => r.Id);
        // Whole, uninterrupted Heat phases that start close to profile (a warm start settles within a few minutes).
        var heats = run.Segments.Where(s => s.Line == "R1" && s.State == "Running" && s.Batch is { Phase: "Heat", From: 0 } b && b.To > 0.99 && Math.Abs(b.StartC - 40) < 8).ToList();
        var golden = heats.Where(s => Strength(drift, s.Start, cleaned) == 0).ToList();
        var drifted = heats.Where(s => Strength(drift, s.Start, cleaned) >= 1).ToList();
        Assert.True(golden.Count >= 8 && drifted.Count >= 4, $"{golden.Count} golden and {drifted.Count} drifted heats");
        double Mid(PlantLineSegment s, PlantTag tag) => (double)signals.ValueAt(tag, s.Start + (s.End - s.Start) / 2)!;
        foreach (var s in drifted)
        {
            double setpoint = recipes[s.Batch!.Recipe].Phases[1].TemperatureC.At(0.5);
            Assert.Equal(drift.TemperatureLagC, s.Batch.LagC, 3);
            Assert.InRange(setpoint - Mid(s, temperature), drift.TemperatureLagC - 1.5, drift.TemperatureLagC + 1.5);
        }
        foreach (var s in golden)
            Assert.InRange(recipes[s.Batch!.Recipe].Phases[1].TemperatureC.At(0.5) - Mid(s, temperature), -1.5, 1.5);
        // Heating rate at a quarter of the phase (where the lag shape also slows it): clearly lower while drifted.
        double Quarter(PlantLineSegment s) => (double)signals.ValueAt(heatRate, s.Start + (s.End - s.Start) / 4)!;
        double PerRecipe(List<PlantLineSegment> list, string recipe) => list.Where(s => s.Batch!.Recipe == recipe).Select(Quarter).DefaultIfEmpty(double.NaN).Average();
        foreach (var recipe in recipes.Keys.Where(r => drifted.Any(s => s.Batch!.Recipe == r) && golden.Any(s => s.Batch!.Recipe == r)))
            Assert.True(PerRecipe(drifted, recipe) < 0.75 * PerRecipe(golden, recipe), $"{recipe}: {PerRecipe(drifted, recipe)} vs {PerRecipe(golden, recipe)} °C/min");
        Assert.Equal(0.0, signals.ValueAt(heatRate, run.Segments.First(s => s.Line == "R1" && s.Batch?.Phase == "Cool").Start.AddMinutes(30)));
        // After discharge, BatchDeviation reads the batch's final deviation score.
        foreach (var batch in PlantPreview.FinalTables(run)["batches"].Where(b => b["end_utc"] is DateTimeOffset end && end < run.EndUtc.AddMinutes(-1)))
            Assert.Equal((double)batch["deviation_score"]!, (double)signals.ValueAt(deviation, Time(batch["end_utc"]).AddSeconds(1))!, 3);
    }

    /// <summary>After a repair the unit reheats at most ReheatRateC per minute from where it cooled to, instead of jumping back to the setpoint.</summary>
    [Fact]
    public void RepairedPhaseReheatsGraduallyFromWhereItCooled()
    {
        var run = PlantEngine.Run(PlantBatchTests.Model("chemicals-batch"), Anchor.AddDays(3));
        var signals = new PlantSignals(run);
        var temperature = signals.Tags.Single(t => t.Name == "ChemicalsBatch.R1.Temperature");
        var segments = run.Segments.Where(s => s.Line == "R1").ToList();
        int checkedRepairs = 0;
        for (int i = 1; i < segments.Count; i++)
        {
            var (down, after) = (segments[i - 1], segments[i]);
            if (down.State != "Down" || down.Batch is null || after.State != "Running" || after.Batch is not { } resumed || (down.End - down.Start).TotalMinutes < 60) continue;
            var phase = run.Model.Lines[0].Batch!.Recipes.Single(r => r.Id == resumed.Recipe).Phases[resumed.Sequence - 1];
            double target = PlantBatchThermal.Target(phase, resumed.LagC, resumed.From);
            if (target - resumed.StartC < 40) continue; // only stops long and hot enough to need a real reheat
            double before = (double)signals.ValueAt(temperature, after.Start.AddSeconds(-10))!;
            double start = (double)signals.ValueAt(temperature, after.Start)!;
            Assert.InRange(start - before, -1.5, 1.5); // no jump at the restart
            double tenMinutes = (double)signals.ValueAt(temperature, after.Start.AddMinutes(10))!;
            Assert.InRange(tenMinutes - resumed.StartC, 30 - 1, 30 + 1); // 3 °C per minute
            Assert.True(tenMinutes < target - 5, $"{tenMinutes} still below the setpoint {target}");
            checkedRepairs++;
        }
        Assert.True(checkedRepairs >= 1, "the chemicals base should have a long hot stop");
    }

    /// <summary>
    /// 5b: drifted syrup passes its wide release limits, but the filler lots made
    /// from it fail the finished-drink Brix spec. Every held lot traces through
    /// batchconsumption to at least one drifted syrup batch, and no lot made only
    /// from undrifted syrup is ever held.
    /// </summary>
    [Fact]
    public void SyrupDriftHoldsFillerLotsThatTraceBackToDriftedBatches()
    {
        var run = Run("golden-batch-syrup-holds");
        var drift = run.Model.Drifts.First();
        var tables = PlantPreview.FinalTables(run);
        var batches = tables["batches"].ToDictionary(b => (string)b["batch_id"]!);
        bool Drifted(string batch) => Strength(drift, Time(batches[batch]["start_utc"]), null) > 0;
        var holds = tables["qualityholds"];
        Assert.DoesNotContain(holds, h => h["batch_id"] is not null); // the syrup itself is always released
        Assert.True(holds.Count >= 4, $"{holds.Count} filler holds");
        Assert.All(holds, h => Assert.StartsWith("Brix", (string)h["reason"]!));
        var held = holds.Select(h => (string)h["lot_id"]!).ToHashSet();
        int clean = 0;
        foreach (var lot in tables["lots"].Where(l => Equals(l["line_id"], "L1") && tables["qualitysamples"].Any(s => Equals(s["lot_id"], l["lot_id"]) && s["passed"] is bool)))
        {
            var used = tables["batchconsumption"].Where(c => Equals(c["production_order"], lot["production_order"])).Select(c => (string)c["batch_id"]!).ToList();
            Assert.NotEmpty(used);
            if (held.Contains((string)lot["lot_id"]!)) Assert.Contains(used, Drifted);
            else if (used.All(b => !Drifted(b))) clean++;
            if (used.All(b => !Drifted(b))) Assert.DoesNotContain((string)lot["lot_id"]!, held);
        }
        Assert.True(clean >= 10, $"{clean} lots from undrifted syrup");
        // The drifted batches ran Dissolve longer, yielded less, and tested lower on release.
        var results = tables["qualityresults"].ToDictionary(r => (string)r["sample_id"]!);
        var syrupBrix = tables["qualitysamples"].Where(s => s["batch_id"] is string).ToDictionary(s => (string)s["batch_id"]!, s => (double)results[(string)s["sample_id"]!]["value"]!);
        var fullStrength = batches.Values.Where(b => b["end_utc"] is DateTimeOffset && Strength(drift, Time(b["start_utc"]), null) >= 1).Select(b => (string)b["batch_id"]!).ToList();
        var undrifted = batches.Values.Where(b => b["end_utc"] is DateTimeOffset && !Drifted((string)b["batch_id"]!)).Select(b => (string)b["batch_id"]!).ToList();
        Assert.True(fullStrength.Count >= 3, $"{fullStrength.Count} syrup batches at full drift strength");
        Assert.True(fullStrength.Average(b => syrupBrix[b]) < undrifted.Average(b => syrupBrix[b]) - 0.25);
        Assert.True(fullStrength.Average(b => (double)batches[b]["yield_percent"]!) < 94.5);
        // The filler's analyzer reads the low Brix while it runs a held lot's order.
        var signals = new PlantSignals(run);
        var brix = signals.Tags.Single(t => t.Name == "BeverageSyrup.L1.Quality.Brix");
        var heldOrders = tables["lots"].Where(l => held.Contains((string)l["lot_id"]!)).Select(l => (string)l["production_order"]!).ToHashSet();
        var running = run.Segments.Where(s => s.Line == "L1" && s.State == "Running" && s.Order is { } o && heldOrders.Contains(o) && (s.End - s.Start).TotalMinutes > 10).ToList();
        Assert.NotEmpty(running);
        Assert.All(running, s => Assert.True((double)signals.ValueAt(brix, s.Start.AddMinutes(5))! < 10.85));
    }

    /// <summary>
    /// 5c: every batch off the golden profile has a deviation; its batch is
    /// released only after a QA person closes the deviation, and its lot only
    /// after all its batches are released.
    /// </summary>
    [Fact]
    public void PharmaDeviationsHoldReleaseUntilQaClosesThem()
    {
        var run = Run("golden-batch-pharma-deviation");
        var tables = PlantPreview.FinalTables(run);
        var deviations = tables["deviations"];
        var qa = tables["employees"].Where(e => Equals(e["role"], "QA")).Select(e => (string)e["employee_id"]!).ToHashSet();
        Assert.Equal(2, qa.Count);
        var discharged = tables["batches"].Where(b => b["end_utc"] is DateTimeOffset).ToList();
        Assert.True(deviations.Count >= 5, $"{deviations.Count} deviations");
        foreach (var batch in discharged)
        {
            var own = deviations.Where(d => Equals(d["batch_id"], batch["batch_id"])).ToList();
            Assert.Equal(batch["golden"] is false ? 1 : 0, own.Count);
            if (own.Count == 0) continue;
            var deviation = own[0];
            Assert.Equal(Time(batch["end_utc"]), Time(deviation["opened_utc"]));
            if (deviation["closed_utc"] is DateTimeOffset closed)
            {
                Assert.Contains((string)deviation["qa_approver_id"]!, qa);
                if (batch["released_utc"] is DateTimeOffset released) Assert.True(released >= closed);
            }
            else Assert.Null(batch["released_utc"]);
        }
        // In the change stream (what Connect sees), a deviated batch never turns Released before its deviation closes.
        foreach (var deviation in deviations)
        {
            var release = run.Changes.FirstOrDefault(c => c.Table == "batches" && c.Key == (string)deviation["batch_id"]! && Equals(c.Columns.GetValueOrDefault("status"), "Released"));
            var close = run.Changes.FirstOrDefault(c => c.Table == "deviations" && c.Key == (string)deviation["deviation_id"]! && c.Columns.ContainsKey("closed_utc") && c.Operation == "update");
            if (release is not null) Assert.True(close is not null && close.Sequence < release.Sequence);
        }
        // Lots of granulator orders wait for all their batches.
        var byOrder = tables["batches"].ToLookup(b => (string)b["production_order"]!);
        foreach (var lot in tables["lots"].Where(l => Equals(l["line_id"], "G1") && l["released_utc"] is DateTimeOffset))
            Assert.All(byOrder[(string)lot["production_order"]!], b => Assert.True(b["released_utc"] is DateTimeOffset r && r <= Time(lot["released_utc"])));
        // Deviations name the drifted phase.
        Assert.True(deviations.Count(d => Equals(d["phase"], "WetGranulate")) >= deviations.Count - 1);
    }

    /// <summary>A failed release test holds the batch; it is released after the hold, never consumed or shipped while held.</summary>
    [Fact]
    public void FailedBatchReleaseTestHoldsTheBatchUntilTheHoldIsReleased()
    {
        // Tighten the syrup's release limits so some batches fail.
        var model = PlantBatchTests.Model("beverage-syrup-batch", j =>
        {
            var test = j["quality"]!["batchRelease"]!["parameters"]![0]!;
            test["lower"] = 10.96;
            test["upper"] = 11.04;
        });
        var run = PlantEngine.Run(model, Anchor);
        var tables = PlantPreview.FinalTables(run);
        var holds = tables["qualityholds"].Where(h => h["batch_id"] is string).ToList();
        Assert.True(holds.Count >= 3, $"{holds.Count} batch holds");
        foreach (var hold in holds.Where(h => h["released_utc"] is DateTimeOffset))
        {
            var batch = tables["batches"].Single(b => Equals(b["batch_id"], hold["batch_id"]));
            Assert.Equal(Time(hold["released_utc"]), Time(batch["released_utc"]));
            var consumed = tables["batchconsumption"].Where(c => Equals(c["batch_id"], hold["batch_id"])).ToList();
            Assert.All(consumed, c => Assert.True(Time(c["consumed_utc"]) >= Time(hold["released_utc"])));
        }
        Assert.All(tables["qualitysamples"].Where(s => s["batch_id"] is string), s => Assert.Null(s["lot_id"]));
    }

    [Theory]
    [InlineData("golden-batch-reactor-drift")]
    [InlineData("golden-batch-syrup-holds")]
    [InlineData("golden-batch-pharma-deviation")]
    public void EveryGoldenBatchChangeMatchesThePlantsTableSchema(string storyline)
    {
        var run = Run(storyline);
        var tables = PlantSchema.TablesFor(run.Model).ToDictionary(t => t.Purpose);
        foreach (var change in run.Changes)
        {
            var columns = tables[change.Table].Columns.ToDictionary(c => c.Name, c => c.Type);
            if (change.Operation == "insert") Assert.Equal(columns.Keys.Order(), change.Columns.Keys.Order());
            foreach (var (name, value) in change.Columns)
                if (value is not null)
                    Assert.True(columns[name] switch
                    {
                        "text" => value is string, "timestamptz" => value is DateTimeOffset, "boolean" => value is bool,
                        "bigint" => value is int or long, "numeric" => value is int or long or double or decimal, _ => false
                    }, $"{change.Table}.{name}");
        }
        Assert.Equal(storyline == "golden-batch-pharma-deviation", tables.ContainsKey("deviations"));
        Assert.Contains("goldenprofiles", tables.Keys);
    }

    [Fact]
    public void ConnectSpecAndPreviewCoverGoldenBatches()
    {
        var model = PlantStorylineTests.Build("golden-batch-pharma-deviation");
        var spec = JsonSerializer.Serialize(PlantConnectSpec.Json(model));
        foreach (var name in new[] { "BatchesOffGolden", "GoldenComparison", "BatchDeviations", "pharmabatch_goldenprofiles", "pharmabatch_deviations", "\"Recipe\"" })
            Assert.Contains(name, spec);
        var markdown = PlantConnectSpec.Markdown(model);
        Assert.Contains("`qa_approver_id` → `pharmabatch_employees`", markdown);
        Assert.Contains("`batch_id` → `pharmabatch_batches`", markdown);
        // Packaging plants: no batch_id reference on quality tables, no golden queries.
        var plain = JsonSerializer.Serialize(PlantConnectSpec.Json(PlantCertificationTests.Model([])));
        Assert.DoesNotContain("GoldenComparison", plain);
        Assert.DoesNotContain("batch_id", plain);

        var preview = JsonNode.Parse(JsonSerializer.Serialize(PlantPreview.Build(Run("golden-batch-reactor-drift"), 3600)))!;
        var batches = preview["summary"]!["batches"]!;
        var drift = batches["drifts"]!.AsArray().Single()!;
        Assert.Equal("Heat", (string)drift["phase"]!);
        Assert.True(DateTimeOffset.Parse((string)drift["untilUtc"]!) < Anchor);
        var rows = batches["comparison"]!.AsArray();
        Assert.True(rows.Count > 30);
        Assert.Contains(rows, r => (bool?)r!["golden"] == false);
        Assert.All(rows, r => Assert.True((double)r!["cycleMinutes"]! > 200));
    }
}
