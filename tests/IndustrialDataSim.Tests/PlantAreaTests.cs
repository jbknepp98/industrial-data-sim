using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Plant;

namespace IndustrialDataSim.Tests;

/// <summary>Milestone 5: the Quality and Shipping areas on the same clock as Production.</summary>
public class PlantAreaTests
{
    private static readonly DateTimeOffset Anchor = DateTimeOffset.Parse("2026-11-21T18:00:00Z");
    private static readonly string ExamplePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "plants", "bluebird-bev.json");

    private static JsonObject Example() => JsonNode.Parse(File.ReadAllText(ExamplePath))!.AsObject();

    private static PlantRun Run(JsonObject model)
    {
        var loaded = PlantModelLoader.Load(model.ToJsonString(), Anchor);
        Assert.True(loaded.IsValid, JsonSerializer.Serialize(loaded.Errors));
        return PlantEngine.Run(loaded.Model!, Anchor.AddHours(24));
    }

    private static List<Dictionary<string, object?>> Rows(PlantRun run, string table) =>
        PlantPreview.FinalTables(run).GetValueOrDefault(table, []);

    [Fact]
    public void LabResultsAreTheAnalyzerValueAndOutOfSpecLotsAreHeldThenReleased()
    {
        var run = Run(Example());
        var quality = run.Model.Quality!;
        var lots = Rows(run, "lots").ToDictionary(l => (string)l["lot_id"]!);
        var samples = Rows(run, "qualitysamples");
        var results = Rows(run, "qualityresults");
        var holds = Rows(run, "qualityholds");
        Assert.NotEmpty(holds);
        Assert.Equal(lots.Count, samples.Count); // one sample per lot
        foreach (var sample in samples.Where(s => (string?)s["status"] == "Complete"))
        {
            var lot = lots[(string)sample["lot_id"]!];
            var mine = results.Where(r => (string?)r["sample_id"] == (string?)sample["sample_id"]).ToList();
            Assert.Equal(quality.Parameters.Count, mine.Count);
            foreach (var result in mine)
            {
                var parameter = quality.Parameters.Single(p => p.Name == (string)result["parameter"]!);
                double expected = PlantQualityValues.Value(run.Model, parameter, (string)lot["line_id"]!, (string)lot["sku"]!, (DateTimeOffset)lot["produced_utc"]!);
                Assert.Equal(expected, (double)result["value"]!);
                Assert.Equal(PlantQualityValues.InSpec(parameter, expected), result["in_spec"]);
            }
            bool passed = mine.All(r => r["in_spec"] is true);
            Assert.Equal(passed, sample["passed"]);
            var hold = holds.SingleOrDefault(h => (string?)h["sample_id"] == (string?)sample["sample_id"]);
            Assert.Equal(!passed, hold is not null);
            if (hold?["released_utc"] is DateTimeOffset released)
            {
                Assert.Equal(released, lot["released_utc"]);
                Assert.True(released > (DateTimeOffset)hold["created_utc"]!);
            }
        }
    }

    [Fact]
    public void PlantedExcursionAndSkuOffsetExplainEveryHold()
    {
        var run = Run(Example());
        var quality = run.Model.Quality!;
        var excursion = quality.Excursions.Single();
        var lots = Rows(run, "lots").ToDictionary(l => (string)l["lot_id"]!);
        foreach (var hold in Rows(run, "qualityholds"))
        {
            var lot = lots[(string)hold["lot_id"]!];
            var produced = (DateTimeOffset)lot["produced_utc"]!;
            string reason = (string)hold["reason"]!;
            if (reason.StartsWith("pH", StringComparison.Ordinal))
                Assert.True(produced >= excursion.FromUtc && produced < excursion.UntilUtc, "pH hold outside the supplier-switch window");
            if (reason.StartsWith("Brix", StringComparison.Ordinal))
                Assert.Equal("BB-BR-20", lot["sku"]); // only Berry carries the Brix offset
        }
    }

    [Fact]
    public void ReleasedLotsShipOnceAndStockMatchesTheWarehouseTag()
    {
        var run = Run(Example());
        var shipments = Rows(run, "shipments");
        var lots = Rows(run, "lots");
        var inventory = Rows(run, "inventorytransactions");
        foreach (var shipment in shipments.Where(s => (string?)s["status"] == "Shipped"))
        {
            var lot = lots.Single(l => (string?)l["lot_id"] == (string?)shipment["lot_id"]);
            Assert.Equal("Shipped", lot["status"]);
            Assert.True((DateTimeOffset)shipment["shipped_utc"]! >= (DateTimeOffset)lot["released_utc"]!);
            var sale = Rows(run, "salesorders").Single(s => (string?)s["sales_order"] == (string?)shipment["sales_order"]);
            Assert.Equal("Shipped", sale["status"]);
            Assert.Equal(shipment["shipped_utc"], sale["completed_utc"]);
        }
        Assert.Equal(shipments.Count, shipments.Select(s => s["lot_id"]).Distinct().Count());
        double onHand = inventory.Sum(i => Convert.ToDouble(i["quantity_cases"]));
        Assert.True(onHand >= 0);
        var signals = new PlantSignals(run);
        var tag = signals.Tags.Single(t => t.Measure == "CasesOnHand");
        Assert.Equal(onHand, (double)signals.ValueAt(tag, run.EndUtc.AddSeconds(-1))!);
    }

    [Fact]
    public void AreasAreOptionalAndAddOnlyTheirOwnTablesAndTags()
    {
        var bare = Example();
        bare.Remove("quality");
        bare.Remove("shipping");
        var plain = Run(bare);
        Assert.Equal(PlantSchema.Tables.Select(t => t.Purpose).OrderBy(p => p), PlantSchema.TablesFor(plain.Model).Select(t => t.Purpose).OrderBy(p => p));
        Assert.DoesNotContain(plain.Changes, c => c.Table is "lots" or "qualitysamples" or "shipments");
        Assert.DoesNotContain(new PlantSignals(plain).Tags, t => t.Measure.StartsWith("Quality:") || t.Measure == "CasesOnHand");
        Assert.All(Rows(plain, "salesorders").Where(s => s["completed_utc"] is not null), s => Assert.Equal("Completed", s["status"]));

        var full = Run(Example());
        var purposes = PlantSchema.TablesFor(full.Model).Select(t => t.Purpose).ToHashSet();
        Assert.Superset(new HashSet<string> { "lots", "qualityspecs", "qualitysamples", "qualityresults", "qualityholds", "inventorytransactions", "shipments" }, purposes);
        Assert.All(full.Changes, c => Assert.Contains(c.Table, purposes));
    }

    [Theory]
    [InlineData("bad-spec", "plant.invalid_spec")]
    [InlineData("unknown-parameter", "plant.unknown_parameter")]
    [InlineData("backwards-excursion", "plant.invalid_excursion_time")]
    public void QualityModelErrorsExplainTheFix(string mutation, string code)
    {
        var model = Example();
        switch (mutation)
        {
            case "bad-spec": model["quality"]!["parameters"]![0]!["lower"] = 12; break;
            case "unknown-parameter": model["quality"]!["excursions"]![0]!["parameter"] = "Color"; break;
            case "backwards-excursion": model["quality"]!["excursions"]![0]!["until"] = "-12d"; break;
        }
        var result = PlantModelLoader.Load(model.ToJsonString(), Anchor);
        Assert.Contains(result.Errors, e => e.Code == code);
    }

    [Fact]
    public void ConnectSpecReferencesOnlyRealTablesAndColumns()
    {
        var model = PlantModelLoader.Load(Example().ToJsonString(), Anchor).Model!;
        var spec = JsonSerializer.SerializeToNode(PlantConnectSpec.Json(model))!;
        var tables = PlantSchema.TablesFor(model).ToDictionary(t => PlantSchema.PhysicalName(model, t.Purpose), t => t.Columns.Select(c => c.Name).ToHashSet());
        foreach (var table in spec["tables"]!.AsArray())
        {
            var name = (string)table!["name"]!;
            Assert.Contains(name, tables.Keys);
            foreach (var reference in table["references"]!.AsArray())
            {
                Assert.Contains((string)reference!["column"]!, tables[name]);
                Assert.Contains((string)reference["table"]!, tables.Keys);
            }
        }
        var markdown = PlantConnectSpec.Markdown(model);
        Assert.DoesNotContain("Password=", markdown);
        Assert.Contains("row_version > @watermark", markdown);
        var sensors = spec["atlas"]!["instances"]!.AsArray().Where(i => (string?)i!["type"] == "Sensor").ToList();
        Assert.Equal(new PlantSignals(new PlantRun(model, model.StartUtc, model.StartUtc, [], [], [])).Tags.Count, sensors.Count);
    }
}
