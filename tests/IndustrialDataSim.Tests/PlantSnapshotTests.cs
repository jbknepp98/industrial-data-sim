using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Plant;

namespace IndustrialDataSim.Tests;

/// <summary>
/// Engine snapshots: a run resumed from the state saved at any instant must
/// continue exactly as a run from the plant start, so a long-running or
/// restarted plant never replays its whole history and never changes it.
/// </summary>
public class PlantSnapshotTests
{
    private static readonly DateTimeOffset Anchor = DateTimeOffset.Parse("2026-11-21T18:00:00Z");
    private static readonly string ExamplePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "plants", "bluebird-bev.json");

    private static PlantModel Model(bool areas)
    {
        var json = JsonNode.Parse(File.ReadAllText(ExamplePath))!.AsObject();
        if (!areas) { json.Remove("quality"); json.Remove("shipping"); }
        var result = PlantModelLoader.Load(json.ToJsonString(), Anchor);
        Assert.True(result.IsValid, JsonSerializer.Serialize(result.Errors));
        return result.Model!;
    }

    /// <summary>
    /// "areas" and "core" are Bluebird with and without Quality and Shipping;
    /// "certification" is the beverage base with both certification storylines,
    /// supervisor sign-off and renewals inside the run, so resumes cover
    /// certificate state, pending certificate events and sign-off waits.
    /// </summary>
    private static PlantModel Model(string variant) => variant switch
    {
        "areas" => Model(true),
        "core" => Model(false),
        _ => PlantCertificationTests.Model(["uncertified-technician-closure", "uncertified-operator-run"], json =>
        {
            var certification = json["maintenance"]!["certification"]!;
            certification["operators"]!["whenOperatorUncertified"] = "supervisorSignoff";
            certification["operators"]!["signoffMinutes"] = 40;
            certification["renewals"] = new JsonArray(
                new JsonObject { ["technician"] = "E1008", ["skill"] = "mechanical", ["at"] = "-2d" },
                new JsonObject { ["technician"] = "E1007", ["skill"] = "mechanical", ["at"] = "+1d" });
        })
    };

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static string Hash(PlantRun run) =>
        System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json(new { run.Changes, run.Segments, run.Failures }))));

    /// <summary>
    /// Pins the engine's output. Plants already running replay this history on
    /// every restart; if this test fails, the change would stop them with
    /// plant.history_mismatch. Only change these values together with
    /// PlantEngine.EngineVersion and a release note that plants must be rebuilt.
    /// </summary>
    [Theory]
    [InlineData(true, 24, "94C0203D2EF13BA843E49E163E6044CA0ED39393B3FA0C95930EBC288780E882")]
    [InlineData(true, 168, "F5756EE4F1936B689BC5374E6F0F951C4E7B13B6B78E430F78C1D3ED526D69E5")]
    [InlineData(false, 24, "178358F083D808EBBA2D98BEF0376232E049A5B2D568AC6F778F351B75B3361A")]
    [InlineData(false, 168, "7EF62EDD14B9DAAC2813D2FC90B93E051E27889B54062E4F5F41641B2B44A51A")]
    public void EngineOutputIsUnchangedForPlantsAlreadyRunning(bool areas, int hours, string expected) =>
        Assert.Equal(expected, Hash(PlantEngine.Run(Model(areas), Anchor.AddHours(hours))));

    public static TheoryData<string, double> Instants() => new()
    {
        { "areas", 0.5 }, { "areas", 13.25 }, { "areas", 101 }, { "areas", 333.0167 }, { "areas", 480 },
        { "core", 7 }, { "core", 250.5 }, { "core", 470 },
        { "certification", 0.25 }, { "certification", 50.5 }, { "certification", 98 }, { "certification", 120.75 }, { "certification", 200 }
    };

    /// <summary>Hours after the plant start (14 days before the anchor for Bluebird, 6 for the beverage base) at which a snapshot is taken.</summary>
    [Theory]
    [MemberData(nameof(Instants))]
    public void ResumingFromASnapshotReproducesTheFullRunExactly(string variant, double hoursAfterStart)
    {
        var model = Model(variant);
        var end = Anchor.AddDays(7);
        var at = model.StartUtc.AddHours(hoursAfterStart);
        at = new DateTimeOffset(at.UtcTicks - at.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        var full = PlantEngine.Run(model, end);

        var captured = PlantEngine.Run(model, end, null, at);
        Assert.Equal(Hash(full), Hash(captured.Run)); // capturing does not change the run
        var snapshot = captured.Snapshot!;
        Assert.Equal(at, snapshot.AtUtc);
        Assert.Equal(PlantEngine.EngineVersion, snapshot.EngineVersion);
        // The snapshot sits exactly between the changes before and after it.
        Assert.All(full.Changes.Where(c => c.Sequence <= snapshot.ChangeSequence), c => Assert.True(c.Utc < at));
        Assert.All(full.Changes.Where(c => c.Sequence > snapshot.ChangeSequence), c => Assert.True(c.Utc >= at));

        var resumed = PlantEngine.Run(model, end, snapshot, null).Run;
        AssertContinues(full, resumed, at);

        // A snapshot taken by a resumed run resumes exactly too.
        var later = at.AddHours(53).AddSeconds(17);
        if (later < end)
        {
            var second = PlantEngine.Run(model, end, snapshot, later).Snapshot!;
            AssertContinues(full, PlantEngine.Run(model, end, second, null).Run, later);
        }
    }

    private static void AssertContinues(PlantRun full, PlantRun resumed, DateTimeOffset at)
    {
        Assert.Equal(at, resumed.StartUtc);
        Assert.Equal(Json(full.Changes.Where(c => c.Utc >= at)), Json(resumed.Changes));
        Assert.Equal(Json(full.Segments.Where(s => s.End > at)), Json(resumed.Segments));
        Assert.Equal(Json(full.Failures.Where(f => f.RepairedUtc is null || f.RepairedUtc >= at)), Json(resumed.Failures));

        // Every tag, including the cumulative counts and warehouse stock, has the same value.
        var expected = new PlantSignals(full);
        var actual = new PlantSignals(resumed);
        int compared = 0;
        for (var t = at; t < resumed.EndUtc; t = t.AddMinutes(7).AddSeconds(10))
            foreach (var tag in expected.Tags)
            {
                Assert.True(Json(expected.ValueAt(tag, t)!) == Json(actual.ValueAt(tag, t)!), $"{tag.Name} at {t:O}");
                compared++;
            }
        Assert.True(compared > 1000);
    }

    [Fact]
    public void SnapshotsFromAnotherEngineVersionOrDamagedStateAreRefused()
    {
        var model = Model(true);
        var snapshot = PlantEngine.Run(model, Anchor, null, model.StartUtc.AddDays(2)).Snapshot!;
        Assert.Equal(2, snapshot.EngineVersion);
        Assert.Throws<PlantSnapshotException>(() => PlantEngine.Run(model, Anchor, snapshot with { EngineVersion = 1 }, null)); // stored before certification
        Assert.Throws<PlantSnapshotException>(() => PlantEngine.Run(model, Anchor, snapshot with { EngineVersion = PlantEngine.EngineVersion + 1 }, null));
        Assert.Throws<PlantSnapshotException>(() => PlantEngine.Run(model, Anchor, snapshot with { State = "{not json" }, null));
        Assert.Throws<PlantSnapshotException>(() => PlantEngine.Run(model, Anchor, snapshot with { ChangeSequence = snapshot.ChangeSequence + 1 }, null));
        var otherPlant = Model(false);
        Assert.ThrowsAny<Exception>(() => PlantEngine.Run(otherPlant, Anchor, snapshot, null));
        // A certification plant's snapshot does not fit the same plant without the section, and vice versa.
        var certified = Model("certification");
        var certifiedSnapshot = PlantEngine.Run(certified, Anchor, null, certified.StartUtc.AddDays(2)).Snapshot!;
        var withoutSection = PlantCertificationTests.Model([]);
        Assert.Throws<PlantSnapshotException>(() => PlantEngine.Run(withoutSection, Anchor, certifiedSnapshot, null));
        var plainSnapshot = PlantEngine.Run(withoutSection, Anchor, null, withoutSection.StartUtc.AddDays(2)).Snapshot!;
        Assert.Throws<PlantSnapshotException>(() => PlantEngine.Run(certified, Anchor, plainSnapshot, null));
    }

    [Fact]
    public void SnapshotInstantsMustLieInsideTheRun()
    {
        var model = Model(true);
        Assert.Throws<ArgumentOutOfRangeException>(() => PlantEngine.Run(model, Anchor, null, model.StartUtc));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlantEngine.Run(model, Anchor, null, Anchor));
        var snapshot = PlantEngine.Run(model, Anchor, null, model.StartUtc.AddDays(2)).Snapshot!;
        Assert.Throws<ArgumentOutOfRangeException>(() => PlantEngine.Run(model, snapshot.AtUtc, snapshot, null));
    }

    [Fact]
    public void ResumedRunCostDoesNotGrowWithPlantAge()
    {
        // A day resumed from a snapshot near the end holds only that day's work.
        var model = Model(true);
        var end = Anchor.AddDays(7);
        var snapshot = PlantEngine.Run(model, end, null, end.AddDays(-1)).Snapshot!;
        var day = PlantEngine.Run(model, end, snapshot, null).Run;
        var full = PlantEngine.Run(model, end);
        Assert.True(day.Changes.Count * 10 < full.Changes.Count, $"{day.Changes.Count} of {full.Changes.Count}");
        Assert.True(day.Segments.Count * 10 < full.Segments.Count);
    }
}
