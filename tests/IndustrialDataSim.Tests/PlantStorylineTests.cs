using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Plant;

namespace IndustrialDataSim.Tests;

/// <summary>
/// The storyline library: every available storyline builds a valid plant that
/// actually shows its story, storylines combine, and every talk-track step
/// names tags and tables that exist in the built plant.
/// </summary>
public class PlantStorylineTests
{
    private static readonly DateTimeOffset Anchor = DateTimeOffset.Parse("2026-11-21T18:00:00Z");
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "storylines");

    internal static PlantStorylineLibrary Library()
    {
        var result = PlantStorylines.Load(File.ReadAllText(Path.Combine(Root, "storylines.json")));
        Assert.True(result.IsValid, JsonSerializer.Serialize(result.Errors));
        return result.Library!;
    }

    private static string BaseJson(string id) => File.ReadAllText(Path.Combine(Root, "bases", id + ".json"));

    internal static PlantModel Build(params string[] ids)
    {
        var library = Library();
        var storylines = ids.Select(id => library.Find(id)!).ToList();
        string baseId = PlantStorylines.CommonBase(storylines)!;
        var composed = PlantStorylines.Compose(baseId, BaseJson(baseId), storylines);
        Assert.True(composed.IsValid, JsonSerializer.Serialize(composed.Errors));
        var loaded = PlantModelLoader.Load(composed.ModelJson!, Anchor);
        Assert.True(loaded.IsValid, JsonSerializer.Serialize(loaded.Errors));
        return loaded.Model!;
    }

    public static TheoryData<string> AvailableIds()
    {
        var data = new TheoryData<string>();
        foreach (var s in Library().Storylines.Where(s => s.Available)) data.Add(s.Id);
        return data;
    }

    [Fact]
    public void LibraryHoldsTheNeedsAnalysisStorylines()
    {
        var library = Library();
        Assert.Equal(15, library.Storylines.Count);
        Assert.Equal(8, library.Storylines.Count(s => s.Engine == "available"));
        Assert.Equal(0, library.Storylines.Count(s => s.Engine == "planned"));
        Assert.Equal(7, library.Storylines.Count(s => s.Engine == "later"));
        // Every storyline the engine cannot build yet names what to offer instead (two have no fit).
        Assert.Equal(2, library.Storylines.Count(s => !s.Available && s.Closest is null));
        foreach (var s in library.Storylines.Where(s => s.Available))
            foreach (string baseId in s.Bases)
            {
                var baseModel = JsonNode.Parse(BaseJson(baseId))!.AsObject();
                foreach (var area in s.Requires.Where(a => a is "quality" or "shipping"))
                    Assert.True(baseModel.ContainsKey(area), $"{s.Id} requires {area}, which base {baseId} lacks");
            }
    }

    [Theory]
    [MemberData(nameof(AvailableIds))]
    public void EveryAvailableStorylineBuildsAValidPlantAndNamesRealTagsAndTables(string id)
    {
        var model = Build(id);
        var tags = new PlantSignals(new PlantRun(model, model.StartUtc, model.StartUtc, [], [], [])).Tags.Select(t => t.Name).ToHashSet();
        var tables = PlantSchema.TablesFor(model).Select(t => PlantSchema.PhysicalName(model, t.Purpose)).ToHashSet();
        foreach (var step in Library().Find(id)!.TalkTrack)
        {
            foreach (var tag in step.Tags)
                Assert.Contains(tag.Replace("{TagRoot}", model.TagRoot), tags);
            foreach (var table in step.Tables)
                Assert.Contains(table.Replace("{prefix}", model.TablePrefix), tables);
        }
    }

    public static TheoryData<string, string> Variants()
    {
        var data = new TheoryData<string, string>();
        foreach (var s in Library().Storylines)
            foreach (var v in s.Variants) data.Add(s.Id, v.Base);
        return data;
    }

    /// <summary>A storyline's variant builds on its own base, and its talk track (or the storyline's, when it has none) names real tags and tables there.</summary>
    [Theory]
    [MemberData(nameof(Variants))]
    public void EveryVariantBuildsOnItsBaseAndNamesRealTagsAndTables(string id, string baseId)
    {
        var storyline = Library().Find(id)!;
        var composed = PlantStorylines.Compose(baseId, BaseJson(baseId), [storyline]);
        Assert.True(composed.IsValid, JsonSerializer.Serialize(composed.Errors));
        var loaded = PlantModelLoader.Load(composed.ModelJson!, Anchor);
        Assert.True(loaded.IsValid, JsonSerializer.Serialize(loaded.Errors));
        var model = loaded.Model!;
        var tags = new PlantSignals(new PlantRun(model, model.StartUtc, model.StartUtc, [], [], [])).Tags.Select(t => t.Name).ToHashSet();
        var tables = PlantSchema.TablesFor(model).Select(t => PlantSchema.PhysicalName(model, t.Purpose)).ToHashSet();
        foreach (var step in storyline.TalkTrackFor(baseId))
        {
            foreach (var tag in step.Tags) Assert.Contains(tag.Replace("{TagRoot}", model.TagRoot), tags);
            foreach (var table in step.Tables) Assert.Contains(table.Replace("{prefix}", model.TablePrefix), tables);
        }
    }

    /// <summary>
    /// The beverage audience's plant (phase 8, audience A): late orders, repeat
    /// failures and syrup holds on one two-line plant. Storylines 1 and 2 run
    /// on the syrup-batch base through their variants.
    /// </summary>
    [Fact]
    public void LabelerRepeatFailureAndSyrupHoldsShareTheSyrupBatchPlant()
    {
        var library = Library();
        string[] ids = ["labeler-jams-late-orders", "repeat-bearing-failure", "golden-batch-syrup-holds"];
        Assert.Equal("beverage-syrup-batch", PlantStorylines.CommonBase(ids.Select(i => library.Find(i)!).ToList()));
        Assert.Equal("beverage-2-line", PlantStorylines.CommonBase([library.Find(ids[0])!, library.Find(ids[1])!]));
        Assert.Null(PlantStorylines.CommonBase([library.Find("golden-batch-reactor-drift")!, library.Find(ids[0])!]));

        var model = Build(ids);
        Assert.Equal(["MX1", "L1"], model.Lines.Select(l => l.Id));
        Assert.Equal(2, model.Drifts.Count);
        var run = PlantEngine.Run(model, Anchor);
        Assert.Equal(2, run.Failures.Count(f => f.Mode == "LB202-JAM" && f.Planted));
        Assert.Equal(3, run.Failures.Count(f => f.Mode == "AG101-SEAL" && f.Planted));
        Assert.True(run.Changes.Count(c => c.Table == "partsusage" && (string?)c.Columns["part_id"] == "PRT-AG-SEAL") >= 3);
        Assert.Contains(run.Changes, c => c.Table == "salesorders" && c.Columns.TryGetValue("on_time", out var onTime) && onTime is false);
        Assert.Contains(run.Changes, c => c.Table == "qualityholds" && c.Operation == "insert");
    }

    [Fact]
    public void LoaderRejectsBadVariants()
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "storylines.json")))!.AsObject();
        var list = json["storylines"]!.AsArray();
        var labeler = list.First(s => s!["id"]!.GetValue<string>() == "labeler-jams-late-orders")!;
        labeler["variants"]!.AsArray().Add(new JsonObject { ["base"] = labeler["base"]!.GetValue<string>(), ["fragment"] = new JsonObject() });
        var later = list.First(s => s!["engine"]!.GetValue<string>() == "later")!;
        later["variants"] = new JsonArray(new JsonObject { ["base"] = "beverage-2-line", ["fragment"] = new JsonObject() });
        var bearing = list.First(s => s!["id"]!.GetValue<string>() == "repeat-bearing-failure")!;
        bearing["variants"]![0]!["talkTrack"] = new JsonArray();
        bearing["variants"]![0]!["colour"] = "blue";
        var codes = PlantStorylines.Load(json.ToJsonString()).Errors.Select(e => e.Code).ToList();
        Assert.Contains("storyline.variant_base", codes);
        Assert.Contains("storyline.variants", codes);
        Assert.Contains("storyline.talk_track", codes);
        Assert.Contains("storyline.unknown_field", codes);
    }

    [Fact]
    public void LabelerStorylineShowsPlantedJamsWithTicketsAndLateOrders()
    {
        var model = Build("labeler-jams-late-orders");
        var run = PlantEngine.Run(model, Anchor);
        var jams = run.Failures.Where(f => f.Mode == "LB202-JAM" && f.Planted).ToList();
        Assert.Equal(2, jams.Count);
        Assert.All(jams, j => Assert.True(j.RepairedUtc is not null));
        Assert.Contains(run.Changes, c => c.Table == "maintenancetickets" && c.Operation == "insert" && (string?)c.Columns["failure_code"] == "LB202-JAM");
        Assert.Contains(run.Changes, c => c.Table == "salesorders" && c.Columns.TryGetValue("on_time", out var onTime) && onTime is false);
    }

    [Fact]
    public void BearingStorylineShowsThreeRepeatFailuresWithTheSamePart()
    {
        var model = Build("repeat-bearing-failure");
        var run = PlantEngine.Run(model, Anchor);
        Assert.True(run.Failures.Count(f => f.Mode == "MX101-MOTOR" && f.Planted) >= 3);
        Assert.True(run.Changes.Count(c => c.Table == "partsusage" && (string?)c.Columns["part_id"] == "PRT-MX-BRG") >= 3);
    }

    [Fact]
    public void QualityStorylineHoldsFallInsideTheExcursionWindow()
    {
        var model = Build("quality-hold-traced");
        var run = PlantEngine.Run(model, Anchor);
        var holds = run.Changes.Where(c => c.Table == "qualityholds" && c.Operation == "insert").ToList();
        Assert.NotEmpty(holds);
        var excursion = model.Quality!.Excursions.Single();
        var pHHolds = holds.Where(h => ((string)h.Columns["reason"]!).StartsWith("pH")).ToList();
        Assert.True(pHHolds.Count >= 2, $"{pHHolds.Count} pH holds");
        // A lot is sampled when it is produced and judged within the lab time, so
        // every pH hold traces to production inside the planted window.
        Assert.All(pHHolds, h => Assert.InRange(h.Utc, excursion.FromUtc, excursion.UntilUtc.AddHours(2)));
    }

    [Fact]
    public void TechnicianCertificationStorylineShowsAnUncertifiedSafetyCriticalClosure()
    {
        var model = Build("uncertified-technician-closure");
        var run = PlantEngine.Run(model, Anchor);
        var tables = PlantPreview.FinalTables(run);
        var uncertified = tables["workcompliance"].Where(c => c["certified"] is false).ToList();
        var planted = run.Failures.Single(f => f.Planted && f.Mode == "FL201-VALVE");
        // The planted night-shift valve leak is closed by the technician whose certificate lapsed.
        var ticket = tables["maintenancetickets"].Single(t => Equals(t["failure_code"], "FL201-VALVE") && Equals(t["assigned_utc"], planted.FailedUtc.AddMinutes(model.Maintenance.DetectionMinutes)));
        Assert.Equal("Complete", ticket["status"]);
        Assert.Contains(uncertified, c => Equals(c["ticket_id"], ticket["ticket_id"]) && Equals(c["technician_id"], "E1008"));
        Assert.InRange(planted.FailedUtc, Anchor.AddDays(-3), Anchor.AddDays(-2));
    }

    [Fact]
    public void OperatorCertificationStorylineShowsUncertifiedBerryRunsWithMoreRejects()
    {
        var model = Build("uncertified-operator-run");
        var run = PlantEngine.Run(model, Anchor);
        var tables = PlantPreview.FinalTables(run);
        var runs = tables["productionruns"].ToDictionary(r => (string)r["run_id"]!);
        double Rate(IEnumerable<Dictionary<string, object?>> checks)
        {
            var finished = checks.Select(c => runs[(string)c["run_id"]!]).Where(r => r["good_cases"] is long).ToList();
            return 100.0 * finished.Sum(r => (long)r["reject_cases"]!) / finished.Sum(r => (long)r["good_cases"]! + (long)r["reject_cases"]!);
        }
        var uncertified = tables["operatorcompliance"].Where(c => c["certified"] is false).ToList();
        Assert.True(uncertified.Count >= 2, $"{uncertified.Count} uncertified runs");
        Assert.All(uncertified, c => Assert.Equal("L2", runs[(string)c["run_id"]!]["line_id"]));
        Assert.True(Rate(uncertified) > 3 * Rate(tables["operatorcompliance"].Where(c => c["certified"] is true)));
    }

    /// <summary>The three golden-batch storylines compose on their batch bases and carry their drifts (and 5a its cleaning PM).</summary>
    [Theory]
    [InlineData("golden-batch-reactor-drift", "chemicals-batch", "Heat", 1)]
    [InlineData("golden-batch-syrup-holds", "beverage-syrup-batch", "Dissolve", 2)]
    [InlineData("golden-batch-pharma-deviation", "pharma-batch", "WetGranulate", 1)]
    public void GoldenBatchStorylinesComposeWithTheirDrifts(string id, string baseId, string phase, int drifts)
    {
        Assert.Equal(baseId, Library().Find(id)!.Base);
        var model = Build(id);
        Assert.Equal(drifts, model.Drifts.Count);
        Assert.Contains(model.Drifts, d => d.Phase == phase && d.FromUtc < Anchor && d.Cause.Length > 0);
        Assert.All(model.Drifts, d => Assert.NotNull(model.Lines.Single(l => l.Id == d.Line).Batch));
        Assert.True(model.StartUtc < model.Drifts.Min(d => d.FromUtc).AddDays(-1), "the history starts on golden batches before the drift");
        if (id == "golden-batch-reactor-drift")
        {
            Assert.Equal("PM-JK101-CLEAN", model.Drifts[0].EndsWith);
            Assert.Equal(3, model.Maintenance.Preventive.Count); // the base's PMs plus the storyline's cleaning PM
        }
        if (id == "golden-batch-pharma-deviation")
        {
            Assert.NotNull(model.Quality!.Deviations);
            Assert.Equal(2, model.QualityStaff.Count);
        }
    }

    [Fact]
    public void AvailableStorylinesCombineOnTheSameBase()
    {
        var model = Build("labeler-jams-late-orders", "repeat-bearing-failure", "quality-hold-traced");
        Assert.Equal(5, model.Incidents.Count); // planted events add up
        Assert.Single(model.Quality!.Excursions);
        var everything = Build("labeler-jams-late-orders", "repeat-bearing-failure", "quality-hold-traced", "uncertified-technician-closure", "uncertified-operator-run");
        Assert.Equal(6, everything.Incidents.Count);
        Assert.Equal(3, everything.Maintenance.Certification!.Issued.Count);
        Assert.Equal(2, everything.Maintenance.Certification.Operators!.WorkInstructions.Count);
    }

    [Fact]
    public void ConflictingFragmentsAreReportedNotOverwritten()
    {
        PlantStoryline Story(string id, double due) => new(id, id, "pitch", ["cpg"], ["ci-oee"], ["OEE"], 1, [], "available", [], null,
            "beverage-2-line", new JsonObject { ["demand"] = new JsonObject { ["dueDays"] = new JsonObject { ["minimum"] = due } } }, []);
        var conflict = PlantStorylines.Compose("beverage-2-line", BaseJson("beverage-2-line"), [Story("story-one", 1.0), Story("story-two", 2.0)]);
        var error = Assert.Single(conflict.Errors);
        Assert.Equal("storyline.conflict", error.Code);
        Assert.Equal("$.demand.dueDays.minimum", error.Path);
        Assert.Contains("story-one", error.Message);
        Assert.Contains("story-two", error.Message);

        // The same value from two storylines is agreement, not a conflict; overrides always win.
        var agree = PlantStorylines.Compose("beverage-2-line", BaseJson("beverage-2-line"), [Story("story-one", 1.0), Story("story-two", 1.0)],
            new JsonObject { ["simulation"] = "AcmeNuggs", ["demand"] = new JsonObject { ["dueDays"] = new JsonObject { ["minimum"] = 3.0 } } });
        Assert.True(agree.IsValid);
        var model = JsonNode.Parse(agree.ModelJson!)!;
        Assert.Equal("AcmeNuggs", (string)model["simulation"]!);
        Assert.Equal(3.0, (double)model["demand"]!["dueDays"]!["minimum"]!);
    }

    [Fact]
    public void UnavailableOrMismatchedStorylinesAreRefused()
    {
        var library = Library();
        var later = library.Storylines.First(s => s.Engine == "later");
        var result = PlantStorylines.Compose("beverage-2-line", BaseJson("beverage-2-line"), [later]);
        Assert.Equal("storyline.not_available", Assert.Single(result.Errors).Code);
        var wrongBase = PlantStorylines.Compose("chemicals-batch", BaseJson("beverage-2-line"), [library.Find("labeler-jams-late-orders")!]);
        Assert.Equal("storyline.wrong_base", Assert.Single(wrongBase.Errors).Code);
    }

    [Fact]
    public void LoaderRejectsUnknownFieldsBadListsAndDuplicates()
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "storylines.json")))!.AsObject();
        var list = json["storylines"]!.AsArray();
        list[0]!["colour"] = "blue";
        list[1]!["industries"] = new JsonArray("space");
        list[2]!["id"] = list[3]!["id"]!.GetValue<string>();
        list[4]!["engine"] = "soon";
        list[5]!["closest"] = "mine-fleet-disconnect"; // not available: cannot be offered instead
        var result = PlantStorylines.Load(json.ToJsonString());
        Assert.False(result.IsValid);
        var codes = result.Errors.Select(e => e.Code).ToList();
        Assert.Contains("storyline.unknown_field", codes);
        Assert.Contains("storyline.list_value", codes);
        Assert.Contains("storyline.duplicate_id", codes);
        Assert.Contains("storyline.engine", codes);
        Assert.Contains("storyline.closest", codes);
        Assert.All(result.Errors, e => Assert.StartsWith("$.storylines[", e.Path));
    }
}
