using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Simulation;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Tests;

/// <summary>schemaVersion 2: backfill resolution and sessions without endUtc (1.1.0).</summary>
public class OpenEndedSessionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-21T12:00:00.5Z");

    private static JsonObject Open(JsonObject model)
    {
        model["session"]!["schemaVersion"] = 2;
        model["session"]!.AsObject().Remove("endUtc");
        return model;
    }

    private static JsonObject Backfill(JsonObject model, string amount)
    {
        Open(model);
        model["session"]!.AsObject().Remove("startUtc");
        model["session"]!["backfill"] = amount;
        return model;
    }

    private static string Code(SimulationLoadResult result) => Assert.Single(result.Errors.Take(1)).Code;

    [Fact]
    public void VersionTwoWithoutEndUtcIsOpenEndedAndVersionOneStillRequiresIt()
    {
        var open = SimulationDefinitionLoader.Load(Open(ConstantSimulationTests.Model()).ToJsonString());
        Assert.True(open.IsValid, JsonSerializer.Serialize(open.Errors));
        Assert.True(open.Definition!.Session.OpenEnded);
        Assert.Equal(SessionDefinition.OpenEndUtc, open.Definition.Session.EndUtc);

        var v1 = ConstantSimulationTests.Model();
        v1["session"]!.AsObject().Remove("endUtc");
        Assert.Equal("session.required", Code(SimulationLoadResult(v1)));

        var finite = ConstantSimulationTests.Model();
        finite["session"]!["schemaVersion"] = 2;
        var loaded = SimulationLoadResult(finite);
        Assert.True(loaded.IsValid);
        Assert.False(loaded.Definition!.Session.OpenEnded);
    }

    private static SimulationLoadResult SimulationLoadResult(JsonObject model) => SimulationDefinitionLoader.Load(model.ToJsonString());

    [Theory]
    [InlineData(1000, "72h", "2026-11-18T12:00:00Z")]
    [InlineData(60000, "90m", "2026-11-21T10:30:00Z")]
    [InlineData(500, "7d", "2026-11-14T12:00:00.5000000Z")]
    [InlineData(1000, "45s", "2026-11-21T11:59:15Z")]
    public void BackfillResolvesToAStartAlignedToTheSamplingGrid(int samplingMs, string amount, string expected)
    {
        var model = Backfill(ConstantSimulationTests.Model(), amount);
        model["samplingIntervalMs"] = samplingMs;
        string resolved = BackfillResolver.Resolve(model.ToJsonString(), Now, out var error);
        Assert.Null(error);
        var session = JsonNode.Parse(resolved)!["session"]!;
        Assert.Equal(expected, session["startUtc"]!.GetValue<string>());
        Assert.Null(session["backfill"]);
        var loaded = SimulationDefinitionLoader.Load(resolved);
        Assert.True(loaded.IsValid, JsonSerializer.Serialize(loaded.Errors));
        Assert.True(loaded.Definition!.Session.OpenEnded);
    }

    [Fact]
    public void ModelsWithoutBackfillAreReturnedByteForByte()
    {
        string json = ConstantSimulationTests.Model().ToJsonString();
        Assert.Same(json, BackfillResolver.Resolve(json, Now, out var error));
        Assert.Null(error);
        Assert.Same("{not json", BackfillResolver.Resolve("{not json", Now, out _));
    }

    [Theory]
    [InlineData("72")]
    [InlineData("0h")]
    [InlineData("1.5h")]
    [InlineData("3651d")]
    [InlineData("72 h")]
    [InlineData("72H")]
    public void InvalidBackfillIsRejectedWithACorrection(string amount)
    {
        BackfillResolver.Resolve(Backfill(ConstantSimulationTests.Model(), amount).ToJsonString(), Now, out var error);
        Assert.Equal("session.invalid_backfill", error!.Code);
        Assert.Equal("$.session.backfill", error.Path);
    }

    [Fact]
    public void BackfillConflictsAndUnresolvedBackfillExplainTheFix()
    {
        var both = Open(ConstantSimulationTests.Model());
        both["session"]!["backfill"] = "1h";
        BackfillResolver.Resolve(both.ToJsonString(), Now, out var conflict);
        Assert.Equal("session.start_conflict", conflict!.Code);

        // Loading directly (not through the CLI) cannot resolve a relative start.
        Assert.Equal("session.backfill_unresolved", Code(SimulationLoadResult(Backfill(ConstantSimulationTests.Model(), "1h"))));
        var v1 = Backfill(ConstantSimulationTests.Model(), "1h");
        v1["session"]!["schemaVersion"] = 1;
        Assert.Equal("session.backfill_requires_v2", Code(SimulationLoadResult(v1)));
    }

    [Fact]
    public void OpenEndedManufacturingMustBeWindowed()
    {
        var precompiled = Open(WindowedManufacturingTests.Model());
        var error = Assert.Single(SimulationLoadResult(precompiled).Errors);
        Assert.Equal("$.manufacturing.execution", error.Path);
        Assert.Contains("windowed", error.Message);

        var windowed = Open(WindowedManufacturingTests.Model());
        windowed["manufacturing"]!["execution"] = "windowed";
        Assert.True(SimulationLoadResult(windowed).IsValid);
    }

    [Fact]
    public void OpenEndedSessionsWarnAboutPatternsThatStopChanging()
    {
        var model = Open(RandomIntegerHoldTests.Model());
        var held = SimulationLoadResult(model);
        Assert.True(held.IsValid);
        var warning = Assert.Single(held.Warnings);
        Assert.Equal("simulation.pattern_holds_last", warning.Code);
        Assert.Equal("$.generators[0]", warning.Path);

        model["generators"]![0]!["afterDuration"] = "repeat";
        Assert.Empty(SimulationLoadResult(model).Warnings);
        // Finite sessions never warn: holding the last value is expected there.
        Assert.Empty(SimulationLoadResult(RandomIntegerHoldTests.Model()).Warnings);
        // Constants and single-step schedules do not change anyway.
        Assert.Empty(SimulationLoadResult(Open(ConstantSimulationTests.Model())).Warnings);
    }

    [Fact]
    public void PreviewOfAnOpenEndedSessionShowsItsFirstPart()
    {
        var model = Open(ConstantSimulationTests.Model());
        var preview = DryRun.Generate(ConstantSimulationTests.Load(model));
        Assert.Empty(preview.Errors);
        Assert.Equal(DryRun.MaximumPoints / 3, preview.Data!["Example.Temperature"].Count);
    }

    [Fact]
    public void OpenEndedGenerationNeverPassesTheWallClockAndCannotBeExtended()
    {
        using var files = new RuntimeFixture();
        var model = Open(RuntimeFixture.Model());
        model["session"]!["startUtc"] = "2026-11-21T11:59:00Z";
        var clock = DateTimeOffset.Parse("2026-11-21T12:00:00Z");
        using var runtime = new DurableRuntime(files.Database) { UtcNow = () => clock };
        runtime.AddSession(model.ToJsonString());
        // Even with no fence supplied (an unpaced run), generation stops at "now".
        while (runtime.Generate("session-a").Progressed) { }
        var cursor = runtime.GetSession("session-a").NextSlot;
        Assert.Equal(61 * 3, cursor); // 11:59:00 through 12:00:00 inclusive, three tags.
        clock = clock.AddSeconds(10);
        while (runtime.Generate("session-a").Progressed) { }
        Assert.Equal(cursor + 10 * 3, runtime.GetSession("session-a").NextSlot);
        Assert.Equal(SessionStatus.Ready, runtime.GetSession("session-a").Status);

        var refused = Assert.Throws<RuntimeFailure>(() => runtime.ExtendHorizon("session-a",
            DateTimeOffset.Parse("2026-11-22T00:00:00Z"), 0, "6886f6d2-d0e9-43d7-9406-1c31cb34cb54"));
        Assert.Equal("extension.open_horizon", refused.Error.Code);
    }
}
