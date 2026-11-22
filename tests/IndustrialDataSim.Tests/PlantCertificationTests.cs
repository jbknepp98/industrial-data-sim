using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Plant;

namespace IndustrialDataSim.Tests;

/// <summary>
/// Certification compliance (maintenance.certification): technicians'
/// certificates decide who may take safety-critical work, operators'
/// work-instruction certificates are checked when a run starts, and plants
/// without the section are unchanged.
/// </summary>
public class PlantCertificationTests
{
    internal static readonly DateTimeOffset Anchor = DateTimeOffset.Parse("2026-11-21T18:00:00Z");
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "storylines");

    /// <summary>The beverage base with the given storylines, edited by mutate, as model JSON.</summary>
    internal static JsonObject Json(string[] storylines, Action<JsonObject>? mutate = null)
    {
        var library = PlantStorylines.Load(File.ReadAllText(Path.Combine(Root, "storylines.json"))).Library!;
        var composed = PlantStorylines.Compose("beverage-2-line", File.ReadAllText(Path.Combine(Root, "bases", "beverage-2-line.json")),
            storylines.Select(id => library.Find(id)!).ToList());
        Assert.True(composed.IsValid, JsonSerializer.Serialize(composed.Errors));
        var json = JsonNode.Parse(composed.ModelJson!)!.AsObject();
        mutate?.Invoke(json);
        return json;
    }

    internal static PlantModel Model(string[] storylines, Action<JsonObject>? mutate = null)
    {
        var loaded = PlantModelLoader.Load(Json(storylines, mutate).ToJsonString(), Anchor);
        Assert.True(loaded.IsValid, JsonSerializer.Serialize(loaded.Errors));
        return loaded.Model!;
    }

    private const string Technician = "uncertified-technician-closure", Operator = "uncertified-operator-run";

    private static JsonObject Certification(JsonObject json) => json["maintenance"]!["certification"]!.AsObject();

    private static Dictionary<string, List<Dictionary<string, object?>>> Tables(PlantRun run) => PlantPreview.FinalTables(run);

    /// <summary>The ticket opened for the planted filler valve failure.</summary>
    private static (PlantFailure Failure, Dictionary<string, object?> Ticket) Planted(PlantRun run)
    {
        var tables = Tables(run);
        var failure = run.Failures.Single(f => f.Planted && f.Mode == "FL201-VALVE");
        var downtime = tables["downtimeevents"].Single(d => Equals(d["start_utc"], failure.FailedUtc) && Equals(d["failure_code"], "FL201-VALVE"));
        var ticket = tables["maintenancetickets"].Single(t => Equals(t["downtime_id"], downtime["downtime_id"]));
        return (failure, ticket);
    }

    [Fact]
    public void EngineVersionIsTwo() => Assert.Equal(2, PlantEngine.EngineVersion);

    [Fact]
    public void PlantedIncidentMovesIntoItsShift()
    {
        var model = Model([Technician]);
        var incident = model.Incidents.Single();
        Assert.Equal("CrewB", incident.Shift);
        // -3d from 18:00 is CrewA time (12–24 UTC), so it moves to 02:00, two hours into CrewB.
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T02:00:00Z"), incident.AtUtc);
    }

    [Fact]
    public void ExpiredTechnicianIsNeverChosenWhileACertifiedOneIsFree()
    {
        // A second night mechanic with a current certificate; tickets wait rather than go uncertified.
        var model = Model([Technician], json =>
        {
            json["people"]!["technicians"]!.AsArray().Add(new JsonObject { ["id"] = "E1011", ["name"] = "Sam Ortiz", ["shift"] = "CrewB", ["skills"] = new JsonArray("mechanical") });
            Certification(json)["issued"]!.AsArray().Add(new JsonObject { ["technician"] = "E1011", ["skill"] = "mechanical", ["at"] = "-30d" });
            Certification(json)["whenNoneCertified"] = "wait";
        });
        var run = PlantEngine.Run(model, Anchor);
        var checks = Tables(run)["workcompliance"];
        Assert.NotEmpty(checks);
        Assert.All(checks, c => Assert.True(c["certified"] is true, $"{c["ticket_id"]} went to {c["technician_id"]}: {c["reason"]}"));
        Assert.Contains(checks, c => Equals(c["technician_id"], "E1011"));
        Assert.DoesNotContain(checks, c => Equals(c["technician_id"], "E1008") && Equals(c["skill"], "mechanical"));
    }

    [Fact]
    public void AssignAnywayGivesThePlantedFailureToTheLapsedTechnicianAndRecordsIt()
    {
        var model = Model([Technician]);
        var run = PlantEngine.Run(model, Anchor);
        var (failure, ticket) = Planted(run);
        // Detected after detectionMinutes and assigned at once to the night mechanic.
        Assert.Equal(failure.FailedUtc.AddMinutes(model.Maintenance.DetectionMinutes), ticket["assigned_utc"]);
        Assert.Equal("E1008", ticket["technician_id"]);
        Assert.Equal("Complete", ticket["status"]);
        var check = Tables(run)["workcompliance"].Single(c => Equals(c["ticket_id"], ticket["ticket_id"]));
        Assert.Equal(false, check["certified"]);
        Assert.Equal(true, check["safety_critical"]);
        Assert.Equal("mechanical", check["skill"]);
        Assert.Equal("certificate expired 2026-11-11", check["reason"]); // -375d + 365d = 10 days before the anchor
        Assert.Equal(ticket["assigned_utc"], check["checked_utc"]);
        var record = Tables(run)["trainingrecords"].Single(r => Equals(r["employee_id"], "E1008") && Equals(r["skill"], "mechanical"));
        Assert.Equal("Expired", record["status"]);
        Assert.Contains(Tables(run)["maintenanceticketstatus"], s => Equals(s["ticket_id"], ticket["ticket_id"]) && ((string)s["note"]!).StartsWith("Assigned without a current certificate"));
    }

    [Fact]
    public void WaitHoldsTheTicketForACertifiedTechnicianAndTheDowntimeRunsLonger()
    {
        var anyway = PlantEngine.Run(Model([Technician]), Anchor);
        var waiting = PlantEngine.Run(Model([Technician], json => Certification(json)["whenNoneCertified"] = "wait"), Anchor);
        var (failure, ticket) = Planted(waiting);
        Assert.Equal("E1007", ticket["technician_id"]); // the certified day mechanic
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T12:00:00Z"), ticket["assigned_utc"]); // CrewA's shift start
        Assert.All(Tables(waiting)["workcompliance"], c => Assert.Equal(true, c["certified"]));
        double Downtime(PlantRun run) => (double)Tables(run)["downtimeevents"].Single(d => Equals(d["downtime_id"], Planted(run).Ticket["downtime_id"]))["duration_minutes"]!;
        Assert.True(Downtime(waiting) > Downtime(anyway) + 8 * 60, $"{Downtime(waiting)} vs {Downtime(anyway)}");
        Assert.True(failure.FailedUtc < (DateTimeOffset)ticket["assigned_utc"]!);
    }

    [Fact]
    public void RenewalRestoresCompliance()
    {
        var model = Model([Technician], json => Certification(json)["renewals"] = new JsonArray(
            new JsonObject { ["technician"] = "E1008", ["skill"] = "mechanical", ["at"] = "-5d" },
            new JsonObject { ["technician"] = "E1007", ["skill"] = "mechanical", ["at"] = "-4d" }));
        var run = PlantEngine.Run(model, Anchor);
        var (_, ticket) = Planted(run);
        Assert.Equal("E1008", ticket["technician_id"]);
        var check = Tables(run)["workcompliance"].Single(c => Equals(c["ticket_id"], ticket["ticket_id"]));
        Assert.Equal(true, check["certified"]);
        Assert.Equal("certificate valid until 2027-11-16", check["reason"]);
        var records = Tables(run)["trainingrecords"];
        // Lena's old certificate had already expired; Jake's was still current, so it is marked Renewed.
        Assert.Equal(["Expired", "Current"], records.Where(r => Equals(r["employee_id"], "E1008") && Equals(r["skill"], "mechanical")).Select(r => (string)r["status"]!));
        Assert.Equal(["Renewed", "Current"], records.Where(r => Equals(r["employee_id"], "E1007")).Select(r => (string)r["status"]!));
        var renewed = run.Changes.Single(c => c.Table == "trainingrecords" && c.Operation == "insert" && Equals(c.Columns["employee_id"], "E1008") && c.Utc > model.StartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-11-16T18:00:00Z"), renewed.Utc);
    }

    [Fact]
    public void CertificateExpiringDuringTheRunIsMarkedExpiredAtItsTime()
    {
        var model = Model([Technician], json => Certification(json)["issued"]![0]!["at"] = "-367d"); // Jake expires 2 days before the anchor
        var run = PlantEngine.Run(model, Anchor);
        var expiry = run.Changes.Single(c => c.Table == "trainingrecords" && c.Operation == "update" && Equals(c.Columns["status"], "Expired"));
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T18:00:00Z"), expiry.Utc);
    }

    [Fact]
    public void OperatorRunAnywayWritesComplianceAndRaisesTheRejectRate()
    {
        var model = Model([Operator]);
        var run = PlantEngine.Run(model, Anchor);
        var tables = Tables(run);
        var checks = tables["operatorcompliance"];
        var uncertified = checks.Where(c => c["certified"] is false).ToList();
        Assert.NotEmpty(uncertified);
        Assert.Contains(checks, c => c["certified"] is true);
        Assert.Equal(2, tables["workinstructions"].Count);
        foreach (var check in checks)
        {
            var productionRun = tables["productionruns"].Single(r => Equals(r["run_id"], check["run_id"]));
            Assert.Equal("BB-BR-20", productionRun["sku"]);
            Assert.Equal(productionRun["operator_id"], check["operator_id"]);
            Assert.Equal(productionRun["start_utc"], check["checked_utc"]);
            Assert.Null(check["signoff_utc"]);
            double expected = check["certified"] is true ? 1.2 : 4.5;
            Assert.All(run.Segments.Where(s => s.State == "Running" && s.Order == (string)productionRun["production_order"]!), s => Assert.Equal(expected, s.RejectPercent));
            if (productionRun["good_cases"] is long good && productionRun["reject_cases"] is long rejects)
                Assert.InRange(100.0 * rejects / (good + rejects), expected - 0.3, expected + 0.3);
        }
        Assert.All(uncertified, c =>
        {
            Assert.Equal("E1010", c["operator_id"]);
            Assert.Equal("WI-L2-BERRY", c["instruction_id"]);
            Assert.Equal("certificate expired 2026-11-15", c["reason"]);
        });
        // Runs of SKUs no work instruction covers are not checked.
        Assert.Equal(tables["productionruns"].Count(r => Equals(r["sku"], "BB-BR-20")), checks.Count);
    }

    [Fact]
    public void SupervisorSignoffDelaysTheRunStartBySignoffMinutes()
    {
        var model = Model([Operator], json =>
        {
            var operators = Certification(json)["operators"]!.AsObject();
            operators["whenOperatorUncertified"] = "supervisorSignoff";
            operators["signoffMinutes"] = 45;
        });
        var run = PlantEngine.Run(model, Anchor);
        var tables = Tables(run);
        var uncertified = tables["operatorcompliance"].Where(c => c["certified"] is false).ToList();
        Assert.NotEmpty(uncertified);
        foreach (var check in uncertified)
        {
            var checkedAt = (DateTimeOffset)check["checked_utc"]!;
            Assert.Equal(checkedAt.AddMinutes(45), check["signoff_utc"]);
            var productionRun = tables["productionruns"].Single(r => Equals(r["run_id"], check["run_id"]));
            Assert.Equal(check["signoff_utc"], productionRun["start_utc"]);
            var wait = run.Segments.Single(s => s.State == "Signoff" && s.Start == checkedAt);
            Assert.Equal(checkedAt.AddMinutes(45), wait.End);
            // Signed off, so the run is sanctioned: no reject uplift.
            Assert.All(run.Segments.Where(s => s.State == "Running" && s.Order == (string)productionRun["production_order"]!), s => Assert.Equal(1.2, s.RejectPercent));
        }
        Assert.Equal("Idle", new PlantSignals(run).ValueAt(new PlantSignals(run).Tags.Single(t => t.Name == "BeverageDemo.L2.FL-211.Status"),
            ((DateTimeOffset)uncertified[0]["checked_utc"]!).AddMinutes(10)));
    }

    [Fact]
    public void CertificationWithoutSafetyCriticalWorkOrOperatorsLeavesTheRestOfThePlantUnchanged()
    {
        var plain = PlantEngine.Run(Model([]), Anchor);
        var model = Model([], json => json["maintenance"]!["certification"] = new JsonObject
        {
            ["validDays"] = 365,
            ["issued"] = new JsonArray(new JsonObject { ["technician"] = "E1008", ["skill"] = "mechanical", ["at"] = "-375d" })
        });
        var certified = PlantEngine.Run(model, Anchor);
        static string Strip(IEnumerable<PlantRecordChange> changes) =>
            JsonSerializer.Serialize(changes.Where(c => c.Table != "trainingrecords").Select(c => new { c.Utc, c.Table, c.Key, c.Operation, c.Columns }));
        Assert.Equal(Strip(plain.Changes), Strip(certified.Changes));
        Assert.Equal(JsonSerializer.Serialize(plain.Segments), JsonSerializer.Serialize(certified.Segments));
        Assert.Equal(JsonSerializer.Serialize(plain.Failures), JsonSerializer.Serialize(certified.Failures));
        Assert.DoesNotContain(PlantSchema.TablesFor(model), t => t.Purpose is "workinstructions" or "operatorcompliance");
        Assert.Contains(PlantSchema.TablesFor(model), t => t.Purpose == "workcompliance");
        Assert.DoesNotContain(PlantSchema.TablesFor(Model([])), t => t.Purpose is "trainingrecords" or "workcompliance");
    }

    [Fact]
    public void EveryCertificationChangeMatchesTheTableSchema()
    {
        var run = PlantEngine.Run(Model([Technician, Operator], json => Certification(json)["renewals"] = new JsonArray(
            new JsonObject { ["technician"] = "E1007", ["skill"] = "mechanical", ["at"] = "-4d" })), Anchor);
        foreach (var change in run.Changes.Where(c => c.Table is "trainingrecords" or "workcompliance" or "workinstructions" or "operatorcompliance"))
        {
            var columns = PlantSchema.Table(change.Table).Columns.ToDictionary(c => c.Name, c => c.Type);
            if (change.Operation == "insert") Assert.Equal(columns.Keys.Order(), change.Columns.Keys.Order());
            foreach (var (name, value) in change.Columns)
                if (value is not null)
                    Assert.True(columns[name] switch
                    {
                        "text" => value is string, "timestamptz" => value is DateTimeOffset, "boolean" => value is bool,
                        "bigint" => value is int or long, _ => false
                    }, $"{change.Table}.{name}");
        }
    }

    [Fact]
    public void ConnectSpecAndPreviewCoverCertification()
    {
        var model = Model([Technician, Operator]);
        var spec = JsonSerializer.Serialize(PlantConnectSpec.Json(model));
        foreach (var name in new[] { "ExpiredCertifications", "UncertifiedWork", "UncertifiedRuns", "beveragedemo_workcompliance", "beveragedemo_operatorcompliance" })
            Assert.Contains(name, spec);
        Assert.Contains("`run_id` → `beveragedemo_productionruns`", PlantConnectSpec.Markdown(model));
        var preview = JsonNode.Parse(JsonSerializer.Serialize(PlantPreview.Build(PlantEngine.Run(model, Anchor), 3600)))!["summary"]!;
        Assert.Contains(preview["uncertifiedAssignments"]!.AsArray(), a => (string)a!["failureCode"]! == "FL201-VALVE" && (string)a["technician"]! == "E1008");
        Assert.NotEmpty(preview["uncertifiedRuns"]!.AsArray());
        var plain = JsonNode.Parse(JsonSerializer.Serialize(PlantPreview.Build(PlantEngine.Run(Model([]), Anchor), 3600)))!["summary"]!;
        Assert.Null(plain["uncertifiedAssignments"]);
        Assert.DoesNotContain("UncertifiedWork", JsonSerializer.Serialize(PlantConnectSpec.Json(Model([]))));
    }

    public static TheoryData<string, string> LoaderErrors() => new()
    {
        { "unknown technician", "plant.unknown_certification_target" },
        { "skill the technician lacks", "plant.unknown_certification_target" },
        { "unknown safety-critical code", "plant.unknown_certification_target" },
        { "unknown operator", "plant.unknown_certification_target" },
        { "unknown work-instruction line", "plant.unknown_certification_target" },
        { "SKU the line does not make", "plant.unknown_certification_target" },
        { "unreadable time", "plant.certification_time" },
        { "time not on a whole minute", "plant.certification_time" },
        { "assignAnyway without safety-critical codes", "plant.invalid_certification_policy" },
        { "unknown technician policy", "plant.invalid_certification_policy" },
        { "unknown operator policy", "plant.invalid_certification_policy" },
        { "unknown field", "session.unknown_property" },
        { "unknown incident shift", "plant.unknown_shift" }
    };

    [Theory]
    [MemberData(nameof(LoaderErrors))]
    public void LoaderReportsEachCertificationMistake(string mistake, string code)
    {
        var json = Json([Technician, Operator]);
        var certification = Certification(json);
        var instruction = certification["operators"]!["workInstructions"]![0]!.AsObject();
        switch (mistake)
        {
            case "unknown technician": certification["issued"]![0]!["technician"] = "E9999"; break;
            case "skill the technician lacks": certification["issued"]![0]!["skill"] = "electrical"; break;
            case "unknown safety-critical code": certification["safetyCritical"]!.AsArray().Add("NOPE-1"); break;
            case "unknown operator": instruction["issued"]![0]!["operator"] = "E9999"; break;
            case "unknown work-instruction line": instruction["line"] = "L9"; break;
            case "SKU the line does not make": instruction["skus"] = new JsonArray("BB-XX-20"); break;
            case "unreadable time": certification["issued"]![0]!["at"] = "-3x"; break;
            case "time not on a whole minute": certification["issued"]![0]!["at"] = "-90s"; break;
            case "assignAnyway without safety-critical codes": certification["safetyCritical"] = new JsonArray(); break;
            case "unknown technician policy": certification["whenNoneCertified"] = "maybe"; break;
            case "unknown operator policy": certification["operators"]!["whenOperatorUncertified"] = "maybe"; break;
            case "unknown field": certification["colour"] = "blue"; break;
            case "unknown incident shift": json["incidents"]![0]!["shift"] = "Graveyard"; break;
        }
        var result = PlantModelLoader.Load(json.ToJsonString(), Anchor);
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal(code, error.Code);
        Assert.StartsWith(mistake == "unknown incident shift" ? "$.incidents" : "$.maintenance.certification", error.Path);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }
}
