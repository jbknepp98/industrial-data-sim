using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Interview;

namespace IndustrialDataSim.Tests;

/// <summary>
/// The interviewer and existing plants: the read-only plant tools, a stop or
/// start proposed for the user to confirm, the hand-off of a change to the
/// plant's own interview, and change mode in that interview (name locked,
/// briefed once).
/// </summary>
public class PlantInterviewTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-22T18:00:00Z");
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "storylines");
    private static readonly Guid GalloInterview = Guid.NewGuid();

    private static InterviewLibrary Library() => InterviewLibrary.Load(Folder).Library!;

    private sealed class FakeDirectory : IPlantDirectory
    {
        public JsonArray List() => [new JsonObject { ["simulation"] = "Gallo", ["status"] = "live" }];
        public JsonObject? Describe(string simulation) => simulation == "Gallo" ? new JsonObject { ["simulation"] = "Gallo", ["status"] = "live", ["tagRoot"] = "Gallo" } : null;
        public Guid? InterviewOf(string simulation) => simulation == "Gallo" ? GalloInterview : null;
    }

    private static JsonElement Input(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonObject Content(ToolOutcome outcome) => JsonNode.Parse(outcome.Content)!.AsObject();

    [Fact]
    public void PlantToolsAreOfferedOnlyWithADirectoryAndNeverBuildOrRemove()
    {
        Assert.Equal(InterviewTools.Names, new InterviewTools(Library(), () => Now).Definitions.Select(Name));
        var names = new InterviewTools(Library(), () => Now, new FakeDirectory()).Definitions.Select(Name).ToList();
        Assert.Equal([.. InterviewTools.Names, .. InterviewTools.PlantToolNames], names);
        foreach (var forbidden in new[] { "build", "run", "start", "approve", "admit", "deploy", "write", "sync", "remove", "delete" })
            Assert.DoesNotContain(names, n => n.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Plants that already exist", InterviewPrompt.Build(Library(), withPlants: true));
        Assert.DoesNotContain("Plants that already exist", InterviewPrompt.Build(Library()));

        static string Name(Anthropic.Models.Messages.ToolUnion t) => JsonNode.Parse(JsonSerializer.Serialize(t))!["name"]!.GetValue<string>();
    }

    [Fact]
    public void ListAndDescribeAnswerOnlyForThePagesPlants()
    {
        var tools = new InterviewTools(Library(), () => Now, new FakeDirectory());
        var state = new InterviewState();
        Assert.Equal("Gallo", Content(tools.Execute(InterviewTools.ListPlants, Input(new { }), state))["plants"]![0]!["simulation"]!.GetValue<string>());
        Assert.Equal("Gallo", Content(tools.Execute(InterviewTools.DescribePlant, Input(new { plant = "Gallo" }), state))["tagRoot"]!.GetValue<string>());
        var unknown = tools.Execute(InterviewTools.DescribePlant, Input(new { plant = "BluebirdBev" }), state);
        Assert.True(unknown.IsError);
        Assert.Contains("Its plants are: Gallo", unknown.Content);
    }

    [Fact]
    public void AStopIsOnlyProposedAndAChangeIsHandedToThePlantsInterview()
    {
        var tools = new InterviewTools(Library(), () => Now, new FakeDirectory());
        var state = new InterviewState();
        var proposed = tools.Execute(InterviewTools.ProposePlantAction, Input(new { plant = "Gallo", action = "stop" }), state);
        Assert.False(proposed.IsError);
        Assert.True(proposed.NeedsChanged);
        Assert.Equal("stop", state.Needs[InterviewTools.PendingActionKey]!["action"]!.GetValue<string>());

        var handed = tools.Execute(InterviewTools.ChangePlant, Input(new { plant = "Gallo", request = "add a quality hold story" }), state);
        Assert.False(handed.IsError);
        var handoff = state.Needs[InterviewTools.HandoffKey]!;
        Assert.Equal(GalloInterview.ToString("D"), handoff["interviewId"]!.GetValue<string>());
        Assert.Equal("add a quality hold story", handoff["request"]!.GetValue<string>());

        // Inside Gallo's own interview, a change is made there, not handed off again.
        var own = new InterviewState { LockedSimulation = "Gallo" };
        Assert.True(tools.Execute(InterviewTools.ChangePlant, Input(new { plant = "Gallo", request = "x" }), own).IsError);
    }

    [Fact]
    public void AChangedPlantKeepsItsName()
    {
        var tools = new InterviewTools(Library(), () => Now, new FakeDirectory());
        var state = new InterviewState { LockedSimulation = "Gallo" };
        var renamed = tools.Execute(InterviewTools.ComposeModel, Input(new
        {
            storylines = new[] { "labeler-jams-late-orders" }, simulation = "GalloTwo", backfill = "6d",
            lineNames = Array.Empty<object>(), productNames = Array.Empty<object>(), peopleNames = Array.Empty<object>()
        }), state);
        Assert.True(renamed.IsError);
        Assert.Contains("tool.simulation_locked", renamed.Content);
        var kept = tools.Execute(InterviewTools.ComposeModel, Input(new
        {
            storylines = new[] { "labeler-jams-late-orders" }, simulation = "Gallo", backfill = "6d",
            lineNames = Array.Empty<object>(), productNames = Array.Empty<object>(), peopleNames = Array.Empty<object>()
        }), state);
        Assert.False(kept.IsError, kept.Content);
    }
}
