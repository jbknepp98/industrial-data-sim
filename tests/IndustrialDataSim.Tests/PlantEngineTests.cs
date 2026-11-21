using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Plant;

namespace IndustrialDataSim.Tests;

/// <summary>
/// The Production → Maintenance loop (plant simulation milestone 1). Uses the
/// Bluebird Bev example with a fixed anchor so every run is reproducible.
/// </summary>
public class PlantEngineTests
{
    private static readonly DateTimeOffset Anchor = DateTimeOffset.Parse("2026-11-21T18:00:00Z");
    private static readonly string ExamplePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "plants", "bluebird-bev.json");

    private static JsonObject Example() => JsonNode.Parse(File.ReadAllText(ExamplePath))!.AsObject();

    private static PlantModel Load(JsonObject model, DateTimeOffset? anchor = null)
    {
        var result = PlantModelLoader.Load(model.ToJsonString(), anchor ?? Anchor);
        Assert.True(result.IsValid, JsonSerializer.Serialize(result.Errors));
        return result.Model!;
    }

    private static PlantRun Run(JsonObject? model = null) => PlantEngine.Run(Load(model ?? Example()), Anchor.AddHours(24));

    private static string Fingerprint(PlantRun run) => JsonSerializer.Serialize(new { run.Changes, run.Segments, run.Failures });

    [Fact]
    public void SameModelAndAnchorReproduceTheSameHistoryAndSeedsDiffer()
    {
        Assert.Equal(Fingerprint(Run()), Fingerprint(Run()));
        var other = Example();
        other["seed"] = 7;
        Assert.NotEqual(Fingerprint(Run()), Fingerprint(Run(other)));
    }

    [Fact]
    public void EachLineIsInExactlyOneStateAtEveryInstant()
    {
        var run = Run();
        foreach (var line in run.Model.Lines)
        {
            var list = run.Segments.Where(s => s.Line == line.Id).OrderBy(s => s.Start).ToList();
            Assert.Equal(run.StartUtc, list[0].Start);
            Assert.Equal(run.EndUtc, list[^1].End);
            for (int i = 1; i < list.Count; i++) Assert.Equal(list[i - 1].End, list[i].Start);
            Assert.All(list, s => Assert.True(s.End > s.Start));
        }
    }

    [Fact]
    public void EveryBreakdownIsDetectedTicketedRepairedAndReturnsTheLineToService()
    {
        var run = Run();
        var tables = PlantPreview.FinalTables(run);
        var downtime = tables["downtimeevents"].Where(d => (string?)d["category"] == "Unplanned").ToList();
        var tickets = tables["maintenancetickets"].Where(t => (string?)t["ticket_type"] == "Corrective").ToList();
        int detection = run.Model.Maintenance.DetectionMinutes;
        Assert.NotEmpty(run.Failures);
        foreach (var failure in run.Failures.Where(f => f.FailedUtc.AddMinutes(detection) < run.EndUtc))
        {
            var record = Assert.Single(downtime, d => (DateTimeOffset)d["start_utc"]! == failure.FailedUtc && (string?)d["line_id"] == failure.Line);
            Assert.Equal(failure.Mode, record["failure_code"]);
            Assert.Equal(failure.FailedUtc.AddMinutes(detection), record["detected_utc"]);
            var ticket = Assert.Single(tickets, t => (string?)t["downtime_id"] == (string?)record["downtime_id"]);
            Assert.Equal(record["detected_utc"], ticket["created_utc"]);
            if (failure.RepairedUtc is { } repaired)
            {
                Assert.Equal(repaired, ticket["completed_utc"]);
                Assert.Equal(repaired, record["end_utc"]);
                // Down for exactly the failure-to-repair interval, then working again.
                var down = run.Segments.Where(s => s.Line == failure.Line && s.State == "Down" && s.Start >= failure.FailedUtc && s.End <= repaired).ToList();
                Assert.Equal((repaired - failure.FailedUtc).TotalMinutes, down.Sum(s => (s.End - s.Start).TotalMinutes), 6);
                var after = run.Segments.FirstOrDefault(s => s.Line == failure.Line && s.Start == repaired);
                if (after is not null) Assert.NotEqual("Down", after.State);
            }
        }
    }

    [Fact]
    public void TheTimeSeriesShowsEachBreakdownBeforeTheRecordExists()
    {
        var run = Run();
        var signals = new PlantSignals(run);
        int detection = run.Model.Maintenance.DetectionMinutes;
        foreach (var failure in run.Failures.Where(f => f.FailedUtc.AddMinutes(detection) < run.EndUtc))
        {
            var speed = signals.Tags.Single(t => t.Name.EndsWith($".{failure.Line}.Speed"));
            var unit = signals.Tags.Single(t => t.Name.EndsWith($".{failure.Line}.{failure.Equipment}.Status"));
            // Speed is zero from the failure through detection: the detector's rule.
            for (var t = failure.FailedUtc; t <= failure.FailedUtc.AddMinutes(detection); t = t.AddSeconds(30))
            {
                Assert.Equal(0.0, signals.ValueAt(speed, t));
                Assert.Equal("Faulted", signals.ValueAt(unit, t));
            }
            // Vibration warns before failures that declare a warning period: if the
            // unit ran during the last tenth of the window, it read far above its
            // normal running level (about 2.5 mm/s) just before failing.
            if (failure.WarningHours > 0)
            {
                var vibration = signals.Tags.Single(t => t.Name.EndsWith($".{failure.Line}.{failure.Equipment}.Vibration"));
                var lastRun = run.Segments.LastOrDefault(s => s.Line == failure.Line && s.State == "Running" && s.End <= failure.FailedUtc);
                var late = failure.FailedUtc.AddHours(-failure.WarningHours * 0.1);
                if (lastRun is not null && lastRun.End > late && lastRun.End - lastRun.Start > TimeSpan.FromMinutes(1))
                    Assert.True((double)signals.ValueAt(vibration, lastRun.End.AddSeconds(-30))! > 5.0, "no vibration warning before failure");
            }
        }
    }

    [Fact]
    public void MinorStopsStayBelowTheDetectorAndNeverRaiseRecords()
    {
        var run = Run();
        var minor = run.Segments.Where(s => s.State == "MinorStop").ToList();
        Assert.NotEmpty(minor);
        Assert.All(minor, s => Assert.True((s.End - s.Start).TotalMinutes < run.Model.Maintenance.DetectionMinutes));
        var downtime = PlantPreview.FinalTables(run)["downtimeevents"];
        Assert.DoesNotContain(downtime, d => minor.Any(s => (DateTimeOffset)d["start_utc"]! == s.Start));
    }

    [Fact]
    public void TechniciansWorkOnlyTheirShiftAndSkillsAndOneTicketAtATime()
    {
        var run = Run();
        var tables = PlantPreview.FinalTables(run);
        var people = run.Model.Technicians.ToDictionary(t => t.Id);
        var tickets = tables["maintenancetickets"].Where(t => t["technician_id"] is string).ToList();
        foreach (var ticket in tickets)
        {
            var tech = people[(string)ticket["technician_id"]!];
            var assigned = (DateTimeOffset)ticket["assigned_utc"]!;
            var shift = run.Model.Shifts.Single(s => s.Name == tech.Shift);
            Assert.True((assigned.UtcDateTime.Hour - shift.StartHour + 24) % 24 < shift.Hours, "assigned off shift");
            string code = (string)ticket["failure_code"]!;
            string skill = run.Model.Maintenance.FailureModes.FirstOrDefault(m => m.Code == code)?.Skill
                ?? run.Model.Maintenance.Preventive.Single(p => p.Code == code).Skill;
            Assert.Contains(skill, tech.Skills);
        }
        foreach (var group in tickets.GroupBy(t => (string)t["technician_id"]!))
        {
            var spans = group.Select(t => ((DateTimeOffset)t["assigned_utc"]!, t["completed_utc"] as DateTimeOffset? ?? run.EndUtc)).OrderBy(s => s.Item1).ToList();
            for (int i = 1; i < spans.Count; i++) Assert.True(spans[i].Item1 >= spans[i - 1].Item2, "technician double-booked");
        }
    }

    [Fact]
    public void CompletedOrdersAgreeWithRunsSalesOrdersAndTheGoodCountTag()
    {
        var run = Run();
        var tables = PlantPreview.FinalTables(run);
        var orders = tables["productionorders"].Where(o => (string?)o["status"] == "Completed").ToList();
        Assert.NotEmpty(orders);
        foreach (var order in orders)
        {
            Assert.Equal((long)Math.Round((double)order["quantity_cases"]!), order["good_cases"]);
            var sale = tables["salesorders"].Single(s => (string?)s["sales_order"] == (string?)order["sales_order"]);
            Assert.Equal("Completed", sale["status"]);
            Assert.Equal(order["completed_utc"], sale["completed_utc"]);
            Assert.Equal((DateTimeOffset)sale["completed_utc"]! <= (DateTimeOffset)sale["due_utc"]!, sale["on_time"]);
        }
        var signals = new PlantSignals(run);
        foreach (var line in run.Model.Lines)
        {
            var count = signals.Tags.Single(t => t.Name.EndsWith($".{line.Id}.GoodCount"));
            double atEnd = (double)signals.ValueAt(count, run.EndUtc.AddSeconds(-1))!;
            double completed = orders.Where(o => (string?)o["line_id"] == line.Id).Sum(o => (double)(long)o["good_cases"]!);
            Assert.True(atEnd >= completed - 1, $"{line.Id}: counter {atEnd} below completed output {completed}");
        }
    }

    [Fact]
    public void PlantedIncidentsHappenAtOrAfterTheirTime()
    {
        var run = Run();
        foreach (var incident in run.Model.Incidents)
            Assert.Contains(run.Failures, f => f.Planted && f.Mode == incident.FailureMode && f.FailedUtc >= incident.AtUtc && f.FailedUtc < incident.AtUtc.AddHours(12));
    }

    [Theory]
    [InlineData("three-lines", "plant.invalid_count")]
    [InlineData("unknown-sku", "plant.unknown_sku")]
    [InlineData("unstaffed-skill", "plant.unstaffed_skill")]
    [InlineData("long-minor-stop", "plant.minor_stop_too_long")]
    [InlineData("shift-gap", "plant.shift_coverage")]
    [InlineData("bad-name", "plant.invalid_simulation")]
    [InlineData("unmade-sku", "plant.unmade_sku")]
    public void SmallPlantRulesAreEnforcedWithACorrection(string mutation, string code)
    {
        var model = Example();
        switch (mutation)
        {
            case "three-lines":
                var third = model["lines"]![1]!.DeepClone();
                third["id"] = "L3";
                model["lines"]!.AsArray().Add(third);
                break;
            case "unknown-sku": model["lines"]![0]!["skus"]![0] = "BB-XX-20"; break;
            case "unstaffed-skill": model["maintenance"]!["failureModes"]![0]!["skill"] = "hydraulics"; break;
            case "long-minor-stop": model["maintenance"]!["minorStops"]!["minutes"]!["maximum"] = 9; break;
            case "shift-gap": model["shifts"]![0]!["hours"] = 10; break;
            case "bad-name": model["simulation"] = "bluebird bev"; break;
            case "unmade-sku":
                foreach (var line in model["lines"]!.AsArray()) line!["skus"]!.AsArray().RemoveAt(2);
                break;
        }
        var result = PlantModelLoader.Load(model.ToJsonString(), Anchor);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == code);
        Assert.All(result.Errors, e => Assert.False(string.IsNullOrWhiteSpace(e.Message)));
    }

    [Fact]
    public void BackfillIsResolvedAgainstTheAnchorAndTablesShareTheSimulationPrefix()
    {
        var model = Load(Example());
        Assert.Equal(Anchor.AddDays(-14), model.StartUtc);
        Assert.Null(model.EndUtc);
        Assert.Equal("bluebirdbev_", model.TablePrefix);
        var preview = JsonSerializer.SerializeToNode(PlantPreview.Build(PlantEngine.Run(model, Anchor.AddHours(2)), 300))!;
        Assert.All(preview["tables"]!.AsObject(), t => Assert.StartsWith("bluebirdbev_", t.Key));
        Assert.Contains(preview["tables"]!.AsObject(), t => t.Key == "bluebirdbev_maintenancetickets");
    }
}
