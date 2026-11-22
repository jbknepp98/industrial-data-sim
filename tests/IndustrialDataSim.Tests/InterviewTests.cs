using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Messages;
using IndustrialDataSim.Core.Plant;
using IndustrialDataSim.Interview;
using IndustrialDataSim.Runtime;
using Npgsql;

namespace IndustrialDataSim.Tests;

/// <summary>
/// The interview service (phase 6) with a scripted fake model: the full
/// need-first interview, tool-input validation, the loop's limits and stop
/// reasons, resuming after a restart, the deterministic storyline ranking and
/// the Postgres store (when FLOWSIM_TEST_PG is set).
/// </summary>
public class InterviewTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-21T18:00:00Z");
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "storylines");

    private static InterviewLibrary Library()
    {
        var (library, errors) = InterviewLibrary.Load(Folder);
        Assert.True(library is not null, JsonSerializer.Serialize(errors));
        return library!;
    }

    // ---------------- fake model ----------------

    /// <summary>Replies from a script, one per call. Each step can inspect the request it answers.</summary>
    private sealed class ScriptedModel(params Func<GageMessageRequest, Message>[] steps) : IInterviewModel
    {
        private int next;
        public List<GageMessageRequest> Requests { get; } = [];
        public int Calls => next;

        public Task<Message> CreateMessage(GageMessageRequest request, CancellationToken stop)
        {
            Requests.Add(request);
            if (next >= steps.Length) throw new InvalidOperationException("The script has no more replies.");
            return Task.FromResult(steps[next++](request));
        }
    }

    private static Message Reply(string stopReason, params JsonObject[] blocks) => JsonSerializer.Deserialize<Message>(new JsonObject
    {
        ["id"] = "msg_test", ["type"] = "message", ["role"] = "assistant", ["model"] = "anthropic/claude",
        ["content"] = new JsonArray(blocks), ["stop_reason"] = stopReason, ["stop_sequence"] = null,
        ["usage"] = new JsonObject { ["input_tokens"] = 1000, ["output_tokens"] = 100, ["cache_read_input_tokens"] = 400, ["cache_creation_input_tokens"] = 0 }
    }.ToJsonString())!;

    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    private static int toolCounter;
    private static JsonObject Call(string name, object input) => new()
    {
        ["type"] = "tool_use", ["id"] = "toolu_" + Interlocked.Increment(ref toolCounter), ["name"] = name, ["input"] = JsonSerializer.SerializeToNode(input)
    };

    /// <summary>The tool_result blocks of the request's last message (the results of the previous round).</summary>
    private static List<JsonObject> LastResults(GageMessageRequest request)
    {
        var last = JsonNode.Parse(JsonSerializer.Serialize(request.Messages[^1]))!.AsObject();
        Assert.Equal("user", last["role"]!.GetValue<string>());
        return last["content"]!.AsArray().OfType<JsonObject>().Where(b => b["type"]!.GetValue<string>() == "tool_result").ToList();
    }

    private static JsonObject ResultJson(JsonObject block) => JsonNode.Parse(block["content"]!.GetValue<string>())!.AsObject();
    private static bool IsError(JsonObject block) => block["is_error"]?.GetValue<bool>() == true;

    private static readonly object BeverageAudience = new
    {
        industry = "food-beverage", product = "soft drinks on two bottling lines", roles = new[] { "maintenance", "plant-manager" },
        kpis = new[] { "on-time delivery", "MTTR" }, pains = new[] { "late orders after labeler jams" }, systems = new[] { "SAP PM", "Ignition" },
        constraints = Array.Empty<string>(), historyDays = 6, requestedStories = Array.Empty<string>()
    };

    private static object Compose(string simulation, object[]? lineNames = null) => new
    {
        storylines = new[] { "labeler-jams-late-orders" }, simulation, backfill = "6d",
        lineNames = lineNames ?? [], productNames = Array.Empty<object>(), peopleNames = Array.Empty<object>()
    };

    private static InterviewSession Start(IInterviewModel model, IInterviewStore? store = null, InterviewOptions? options = null) =>
        InterviewSession.Start(Library(), model, store ?? new InMemoryInterviewStore(), options, () => Now);

    // ---------------- the scripted interview ----------------

    [Fact]
    public async Task ScriptedBeverageInterviewEndsWithAValidDraftAndPreview()
    {
        string? rankedFirst = null;
        JsonObject? composeError = null, preview = null;
        var model = new ScriptedModel(
            _ => Reply("tool_use", Text("Thanks, let me note that."), Call(InterviewTools.RecordAudience, BeverageAudience)),
            r =>
            {
                Assert.False(IsError(LastResults(r).Single()));
                return Reply("tool_use", Call(InterviewTools.FindStorylines, new { keywords = new[] { "late orders", "labeler jams" }, askedFor = Array.Empty<object>() }));
            },
            r =>
            {
                rankedFirst = ResultJson(LastResults(r).Single())["storylines"]![0]!["id"]!.GetValue<string>();
                return Reply("end_turn", Text("The best fit is the labeler jam that makes orders late. What should the simulation be called?"));
            },
            _ => Reply("tool_use", Call(InterviewTools.ComposeModel, Compose("ozark bottling"))),
            r =>
            {
                var result = LastResults(r).Single();
                Assert.True(IsError(result));
                composeError = ResultJson(result);
                return Reply("tool_use", Call(InterviewTools.ComposeModel, Compose("OzarkBottling", [new { id = "L1", name = "Bottling Line 1" }])));
            },
            r =>
            {
                var result = LastResults(r).Single();
                Assert.False(IsError(result), result.ToJsonString());
                Assert.True(ResultJson(result)["valid"]!.GetValue<bool>());
                return Reply("tool_use", Call(InterviewTools.PreviewPlant, new { hours = 24 }));
            },
            r =>
            {
                var result = LastResults(r).Single();
                Assert.False(IsError(result), result.ToJsonString());
                preview = ResultJson(result);
                return Reply("end_turn", Text("Here is the preview. Review it and click Approve and run when it looks right."));
            });
        var events = new List<InterviewEvent>();
        var store = new InMemoryInterviewStore();
        var session = Start(model, store);

        var first = await session.Send("I'm showing a bottler's maintenance manager and plant manager. Late orders and labeler jams hurt most.", events.Add);
        Assert.Equal(InterviewOutcome.Completed, first.Outcome);
        Assert.Equal("labeler-jams-late-orders", rankedFirst);

        var second = await session.Send("Call it Ozark Bottling, and line 1 is the Bottling Line 1.", events.Add);
        Assert.Equal(InterviewOutcome.Completed, second.Outcome);
        Assert.Equal(7, model.Calls);
        Assert.Equal(3, second.ToolRounds);

        // The invalid first try came back as loader field errors.
        Assert.Contains(composeError!["errors"]!.AsArray(), e => e!["path"]!.GetValue<string>() == "model $.simulation");

        // The draft is valid, renamed, and the preview is stored and shows the storyline.
        var loaded = PlantModelLoader.Load(session.State.DraftModelJson!, Now);
        Assert.True(loaded.IsValid);
        Assert.Equal("OzarkBottling", loaded.Model!.Simulation);
        Assert.Equal("Bottling Line 1", loaded.Model.Lines[0].Name);
        Assert.NotNull(session.State.PreviewJson);
        var jams = preview!["storylines"]![0]!["moments"]!.AsArray().Where(m => m!["kind"]!.GetValue<string>() == "breakdown").ToList();
        Assert.Equal(2, jams.Count);
        Assert.All(jams, j => Assert.NotEmpty(j!["tickets"]!.AsArray()));
        Assert.Equal(["labeler-jams-late-orders"], session.State.Needs["chosenStorylines"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("food-beverage", session.State.Needs["audience"]!["industry"]!.GetValue<string>());

        // Events for the page.
        Assert.Contains(events, e => e.Kind == InterviewEventKind.PreviewReady);
        Assert.Contains(events, e => e is { Kind: InterviewEventKind.ToolActivity, Text: "Checking the plant model…" });
        Assert.Contains(events, e => e.Kind == InterviewEventKind.AssistantText && e.Text.Contains("Approve and run"));

        // Everything is persisted; usage is recorded per reply and summed.
        var record = store.Load(session.Id)!;
        Assert.Equal(InterviewStatus.Open, record.Status);
        Assert.Equal(Enumerable.Range(1, record.Messages.Count), record.Messages.Select(m => m.Sequence));
        Assert.Equal(7, record.Messages.Count(m => m.Role == "assistant"));
        Assert.Equal(new InterviewUsage(7000, 700, 2800, 0), session.TotalUsage);
        Assert.NotNull(record.PreviewJson);
        Assert.Equal(session.State.DraftModelJson, record.DraftModelJson);
    }

    [Fact]
    public async Task RequestsUseOnlyPortableFeaturesAndAStableSystemPrompt()
    {
        var model = new ScriptedModel(_ => Reply("end_turn", Text("Hello.")), _ => Reply("end_turn", Text("Hello again.")));
        var one = Start(model);
        await one.Send("Hi");
        var two = Start(model);
        await two.Send("Hi");

        var request = model.Requests[0];
        Assert.Equal(16000, request.MaxTokens);
        // Identical for every session (it caches), and free of per-session facts.
        Assert.Equal(model.Requests[0].System, model.Requests[1].System);
        Assert.DoesNotContain("2026-11-21", request.System);
        Assert.DoesNotContain(one.Id.ToString(), request.System);
        // The session facts and date ride in the first user turn instead.
        string firstUser = JsonSerializer.Serialize(request.Messages[0]);
        Assert.Contains("2026-11-21 18:00 UTC", firstUser);
        Assert.Contains(one.Id.ToString(), firstUser);
        // No assistant prefill: every request ends with a user message.
        Assert.All(model.Requests, r => Assert.Equal("user", JsonNode.Parse(JsonSerializer.Serialize(r.Messages[^1]))!["role"]!.GetValue<string>()));
        // The library and the no-build rule are in the prompt.
        Assert.Contains("labeler-jams-late-orders", request.System);
        Assert.Contains("Approve and run", request.System);
    }

    [Fact]
    public void NoBuildOrRunToolIsExposed()
    {
        var tools = new InterviewTools(Library(), () => Now);
        var definitions = tools.Definitions.Select(t => JsonNode.Parse(JsonSerializer.Serialize(t))!.AsObject()).ToList();
        var names = definitions.Select(d => d["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["record_audience", "find_storylines", "compose_model", "preview_plant"], names);
        foreach (var forbidden in new[] { "build", "run", "start", "approve", "admit", "deploy", "write", "sync", "remove" })
            Assert.DoesNotContain(names, n => n.Contains(forbidden, StringComparison.OrdinalIgnoreCase));

        // Strict schemas: every field required, nothing extra allowed, recursively.
        foreach (var definition in definitions)
        {
            Assert.True(definition["strict"]!.GetValue<bool>());
            AssertStrict(definition["input_schema"]!.AsObject());
        }

        static void AssertStrict(JsonObject schema)
        {
            if (schema["type"]?.GetValue<string>() == "object")
            {
                Assert.False(schema["additionalProperties"]!.GetValue<bool>());
                var properties = schema["properties"]!.AsObject().Select(p => p.Key).Order().ToList();
                Assert.Equal(properties, schema["required"]!.AsArray().Select(r => r!.GetValue<string>()).Order().ToList());
                foreach (var (_, property) in schema["properties"]!.AsObject()) AssertStrict(property!.AsObject());
            }
            if (schema["items"] is JsonObject items) AssertStrict(items);
            // Keywords some strict implementations reject are enforced in code instead.
            foreach (var keyword in new[] { "minimum", "maximum", "minItems", "maxItems", "minLength", "maxLength", "pattern" })
                Assert.False(schema.ContainsKey(keyword), keyword);
        }
    }

    // ---------------- robustness ----------------

    [Fact]
    public async Task MalformedToolInputBecomesAnErrorResultNotACrash()
    {
        List<JsonObject>? results = null;
        var model = new ScriptedModel(
            _ => Reply("tool_use",
                Call(InterviewTools.RecordAudience, new { industry = "aerospace", roles = new[] { "wizard" }, extra = 1 }),
                Call("build_plant", new { simulation = "Acme" }),
                Call(InterviewTools.PreviewPlant, new { hours = 500 }),
                Call(InterviewTools.FindStorylines, new { keywords = new[] { "late orders" }, askedFor = Array.Empty<object>() })),
            r => { results = LastResults(r); return Reply("end_turn", Text("Let me fix that.")); });
        var session = Start(model);
        var result = await session.Send("Hello");

        Assert.Equal(InterviewOutcome.Completed, result.Outcome);
        Assert.Equal(4, results!.Count); // every call answered, in one user message
        Assert.All(results, r => Assert.True(IsError(r)));
        var audience = ResultJson(results[0])["errors"]!.AsArray().Select(e => (e!["code"]!.GetValue<string>(), e["path"]!.GetValue<string>())).ToList();
        Assert.Contains(("tool.value_not_allowed", "$.industry"), audience);
        Assert.Contains(("tool.value_not_allowed", "$.roles[0]"), audience);
        Assert.Contains(("tool.unknown_field", "$.extra"), audience);
        Assert.Contains(("tool.missing_field", "$.kpis"), audience);
        Assert.Equal("tool.unknown", ResultJson(results[1])["errors"]![0]!["code"]!.GetValue<string>());
        Assert.Contains("Approve", ResultJson(results[1])["errors"]![0]!["message"]!.GetValue<string>());
        Assert.Equal("$.hours", ResultJson(results[2])["errors"]![0]!["path"]!.GetValue<string>());
        Assert.Equal("tool.audience_missing", ResultJson(results[3])["errors"]![0]!["code"]!.GetValue<string>());
        Assert.Null(session.State.DraftModelJson);
    }

    [Fact]
    public async Task ComposeRejectsUnavailableAndUnknownStorylinesWithTheirFieldPaths()
    {
        JsonObject? result = null;
        var model = new ScriptedModel(
            _ => Reply("tool_use", Call(InterviewTools.ComposeModel, new
            {
                storylines = new[] { "mcc-trip-impact", "no-such-story" }, simulation = "Acme", backfill = "",
                lineNames = Array.Empty<object>(), productNames = Array.Empty<object>(), peopleNames = new[] { new { id = "E9999", name = "Nobody" } }
            })),
            r => { result = ResultJson(LastResults(r).Single()); return Reply("end_turn", Text("OK.")); });
        await Start(model).Send("Hello");
        var errors = result!["errors"]!.AsArray();
        Assert.Contains(errors, e => e!["path"]!.GetValue<string>() == "$.storylines[0]" && e["message"]!.GetValue<string>().Contains("labeler-jams-late-orders"));
        Assert.Contains(errors, e => e!["path"]!.GetValue<string>() == "$.storylines[1]" && e["code"]!.GetValue<string>() == "tool.unknown_storyline");
    }

    [Fact]
    public async Task TheRoundLimitEndsTheTurnAndLeavesAValidHistory()
    {
        var steps = Enumerable.Range(0, 20).Select(_ => (Func<GageMessageRequest, Message>)(r =>
            Reply("tool_use", Call(InterviewTools.FindStorylines, new { keywords = Array.Empty<string>(), askedFor = Array.Empty<object>() })))).ToArray();
        var model = new ScriptedModel(steps);
        var store = new InMemoryInterviewStore();
        var events = new List<InterviewEvent>();
        var session = Start(model, store, new InterviewOptions { MaxToolRounds = 3 });

        var result = await session.Send("Hello", events.Add);
        Assert.Equal(InterviewOutcome.RoundLimit, result.Outcome);
        Assert.Equal(4, model.Calls); // 3 rounds ran; the 4th round's calls were closed, not run
        Assert.Contains(events, e => e.Code == "interview.round_limit");
        var record = store.Load(session.Id)!;
        Assert.Equal(InterviewStatus.Open, record.Status);
        var lastReplayed = JsonNode.Parse(record.Messages.Last(m => m.Role != "notice").ContentJson)!;
        Assert.Equal("user", lastReplayed["role"]!.GetValue<string>());
        Assert.Contains("limit of 3 tool rounds", lastReplayed.ToJsonString());

        // The next user message continues normally.
        var after = new ScriptedModel(_ => Reply("end_turn", Text("Carrying on.")));
        var resumed = InterviewSession.Resume(session.Id, Library(), after, store, clock: () => Now);
        Assert.False(resumed.NeedsContinue);
        Assert.Equal(InterviewOutcome.Completed, (await resumed.Send("continue")).Outcome);
    }

    [Fact]
    public async Task MaxTokensContinuesOnceThenSurfacesTheProblem()
    {
        var once = new ScriptedModel(
            _ => Reply("max_tokens", Text("A very long answer that")),
            r =>
            {
                Assert.Contains("cut off by the length limit", JsonSerializer.Serialize(r.Messages[^1]));
                return Reply("end_turn", Text("…and that is the short version."));
            });
        Assert.Equal(InterviewOutcome.Completed, (await Start(once).Send("Hello")).Outcome);

        var events = new List<InterviewEvent>();
        var twice = new ScriptedModel(_ => Reply("max_tokens", Text("Long")), _ => Reply("max_tokens", Text("Still long")));
        var result = await Start(twice).Send("Hello", events.Add);
        Assert.Equal(InterviewOutcome.LengthLimit, result.Outcome);
        Assert.Contains(events, e => e.Code == "interview.length_limit");

        // A truncated tool call is never run.
        var store = new InMemoryInterviewStore();
        var truncated = new ScriptedModel(
            _ => Reply("max_tokens", Call(InterviewTools.RecordAudience, BeverageAudience)),
            r =>
            {
                Assert.True(IsError(LastResults(r).Single()));
                return Reply("end_turn", Text("Retrying later."));
            });
        var session = Start(truncated, store);
        await session.Send("Hello");
        Assert.False(session.State.Needs.ContainsKey("audience"));
    }

    [Fact]
    public async Task RefusalShowsAPlainMessageAndIsNotReplayed()
    {
        var store = new InMemoryInterviewStore();
        var events = new List<InterviewEvent>();
        var model = new ScriptedModel(_ => Reply("refusal", Text("I can't help with that.")), r => Reply("end_turn", Text("Sure.")));
        var session = Start(model, store);
        Assert.Equal(InterviewOutcome.Refused, (await session.Send("Something odd", events.Add)).Outcome);
        Assert.Contains(events, e => e.Kind == InterviewEventKind.Refusal);
        Assert.DoesNotContain(events, e => e.Kind == InterviewEventKind.AssistantText);
        Assert.DoesNotContain(store.Load(session.Id)!.Messages, m => m.Role == "assistant");
        Assert.Contains(store.Load(session.Id)!.Messages, m => m.Role == "notice" && m.ContentJson.Contains("refusal"));
        await session.Send("Let's talk about the demo instead.");
        Assert.DoesNotContain("can't help", JsonSerializer.Serialize(model.Requests[1].Messages));
    }

    [Fact]
    public async Task GageFailuresExplainAndStop()
    {
        var failing = new FailingModel(new RuntimeFailure("gage.blocked", "Pulse refused the message request for client 'DataSim' (HTTP 403). Ask the Pulse administrator to allow this client."));
        var store = new InMemoryInterviewStore();
        var events = new List<InterviewEvent>();
        var session = Start(failing, store);
        var result = await session.Send("Hello", events.Add);
        Assert.Equal(InterviewOutcome.Stopped, result.Outcome);
        var stopped = Assert.Single(events, e => e.Kind == InterviewEventKind.Stopped);
        Assert.Equal("gage.blocked", stopped.Code);
        Assert.Contains("Pulse administrator", stopped.Text);
        Assert.Equal(1, failing.Calls); // never retried
        Assert.Equal(InterviewStatus.Open, store.Load(session.Id)!.Status);
    }

    private sealed class FailingModel(Exception failure) : IInterviewModel
    {
        public int Calls { get; private set; }
        public Task<Message> CreateMessage(GageMessageRequest request, CancellationToken stop)
        {
            Calls++;
            throw failure;
        }
    }

    // ---------------- resume after a restart ----------------

    /// <summary>Simulates the process dying at a chosen point: the first append matching the predicate throws.</summary>
    private sealed class DyingStore(InMemoryInterviewStore inner, Func<string, bool> dieBefore) : IInterviewStore
    {
        private bool died;
        public void Create(Guid id, DateTimeOffset createdUtc) => inner.Create(id, createdUtc);
        public InterviewRecord? Load(Guid id) => inner.Load(id);
        public void Append(Guid id, IReadOnlyList<(string Role, string ContentJson, InterviewUsage? Usage)> messages, string status)
        {
            if (!died && messages.Any(m => dieBefore(m.ContentJson))) { died = true; throw new OperationCanceledException("killed"); }
            inner.Append(id, messages, status);
        }
        public void SaveState(Guid id, string needsJson, string? draftModelJson, string? previewJson) => inner.SaveState(id, needsJson, draftModelJson, previewJson);
        public void SetStatus(Guid id, string status) => inner.SetStatus(id, status);
        public bool MarkApproved(Guid id, string simulation) => inner.MarkApproved(id, simulation);
        public IReadOnlySet<string> ApprovedSimulations() => inner.ApprovedSimulations();
    }

    [Fact]
    public async Task ResumingAfterAKillBetweenToolCallAndResultRunsTheToolsAndContinues()
    {
        var store = new InMemoryInterviewStore();
        var dying = new DyingStore(store, json => json.Contains("tool_result"));
        var before = new ScriptedModel(_ => Reply("tool_use", Call(InterviewTools.RecordAudience, BeverageAudience)));
        var session = Start(before, dying);
        await Assert.ThrowsAsync<OperationCanceledException>(() => session.Send("A bottler's maintenance manager."));

        // The restarted process finds the turn in progress, with the tool call stored but not answered.
        int storedBefore = store.Load(session.Id)!.Messages.Count;
        var after = new ScriptedModel(r =>
        {
            Assert.Equal(storedBefore + 1, r.Messages.Count); // the whole history, plus the re-run tool results
            Assert.False(IsError(LastResults(r).Single()));
            return Reply("end_turn", Text("Noted. Who else will watch?"));
        });
        var resumed = InterviewSession.Resume(session.Id, Library(), after, store, clock: () => Now);
        Assert.True(resumed.NeedsContinue);
        var result = await resumed.Continue();
        Assert.Equal(InterviewOutcome.Completed, result.Outcome);
        Assert.False(resumed.NeedsContinue);
        Assert.Equal("food-beverage", resumed.State.Needs["audience"]!["industry"]!.GetValue<string>());
        Assert.Equal(1, resumed.UserMessageCount);
    }

    [Fact]
    public async Task ResumingAfterAKillWhileWaitingForTheModelAsksAgain()
    {
        var store = new InMemoryInterviewStore();
        var killed = new FailingModel(new OperationCanceledException("killed"));
        var session = Start(killed, store);
        await Assert.ThrowsAsync<OperationCanceledException>(() => session.Send("Hello"));

        var after = new ScriptedModel(_ => Reply("end_turn", Text("Hello! Who will watch the demo?")));
        var resumed = InterviewSession.Resume(session.Id, Library(), after, store, clock: () => Now);
        Assert.True(resumed.NeedsContinue);
        // Sending a new message first finishes the interrupted turn, so nothing is lost.
        var next = new ScriptedModel(_ => Reply("end_turn", Text("First answer.")), _ => Reply("end_turn", Text("Second answer.")));
        var viaSend = InterviewSession.Resume(session.Id, Library(), next, store, clock: () => Now);
        await viaSend.Send("Plant managers.");
        Assert.Equal(2, next.Calls);
        Assert.Equal(["user", "assistant", "user", "assistant"],
            store.Load(session.Id)!.Messages.Where(m => m.Role != "notice").Select(m => JsonNode.Parse(m.ContentJson)!["role"]!.GetValue<string>()));
    }

    // ---------------- deterministic ranking ----------------

    public static TheoryData<string, string[], string[], string[], string> Profiles() => new()
    {
        { "food-beverage", ["maintenance", "plant-manager"], ["on-time delivery", "MTTR"], ["late orders"], "labeler-jams-late-orders" },
        { "pharma", ["process-engineer"], ["yield", "deviations"], ["batch"], "golden-batch-pharma-deviation" },
        { "chemicals", ["ehs", "executive"], ["certification compliance"], [], "uncertified-technician-closure" },
        { "metals", ["reliability"], ["MTBF"], ["repeat failures"], "repeat-bearing-failure" },
        { "cpg", ["quality"], ["quality holds", "lot genealogy"], [], "quality-hold-traced" },
    };

    [Theory]
    [MemberData(nameof(Profiles))]
    public void RankingIsDeterministicForAProfile(string industry, string[] roles, string[] kpis, string[] keywords, string expectedFirst)
    {
        var library = Library().Storylines;
        var profile = new AudienceProfile(industry, roles, kpis);
        var first = StorylineRanking.Rank(library, profile, keywords);
        var again = StorylineRanking.Rank(library, profile, keywords);
        Assert.Equal(expectedFirst, first[0].Storyline.Id);
        Assert.Equal(first.Select(r => (r.Storyline.Id, r.Score)), again.Select(r => (r.Storyline.Id, r.Score)));
        Assert.True(first.Zip(first.Skip(1)).All(p => p.First.Score >= p.Second.Score));
    }

    [Fact]
    public void PharmaBatchLeadsWithTheGoldenBatchDeviationStoryline()
    {
        var ranked = StorylineRanking.Rank(Library().Storylines, new AudienceProfile("pharma", ["process-engineer"], ["yield", "deviations"]), ["batch"]);
        var top = ranked[0];
        Assert.Equal("golden-batch-pharma-deviation", top.Storyline.Id);
        Assert.Equal("available", top.Storyline.Engine); // phase 4 made it buildable
        Assert.Null(top.OfferInstead);
        Assert.Contains(top.Reasons, r => r.Contains("pharma"));
    }

    [Fact]
    public async Task AnUnsupportedStoryIsMarkedAndAnAvailableOneIsOffered()
    {
        JsonObject? found = null;
        var model = new ScriptedModel(
            _ => Reply("tool_use", Call(InterviewTools.RecordAudience, BeverageAudience)),
            _ => Reply("tool_use", Call(InterviewTools.FindStorylines, new { keywords = new[] { "energy spikes" }, askedFor = new[] { new { storyline = "energy-per-unit-spike", words = "energy spikes" } } })),
            r => { found = ResultJson(LastResults(r).Single()); return Reply("end_turn", Text("That one isn't in the simulator yet.")); });
        var session = Start(model);
        await session.Send("They want to see energy spikes.");
        // The ask is recorded by code from askedFor, for the demo guide.
        Assert.Equal("energy-per-unit-spike", found!["recordedRequests"]![0]!["storyline"]!.GetValue<string>());
        Assert.Equal("energy spikes", InterviewTools.Requests(session.State).Single()!["words"]!.GetValue<string>());
        var energy = found!["storylines"]!.AsArray().Single(s => s!["id"]!.GetValue<string>() == "energy-per-unit-spike")!;
        Assert.False(energy["available"]!.GetValue<bool>());
        // Its library entry names no closest storyline, so the best available match for the audience is offered.
        string offered = energy["offerInstead"]!["id"]!.GetValue<string>();
        Assert.True(Library().Storylines.Find(offered)!.Available);
        Assert.Contains("Nothing in the library is close", energy["offerInstead"]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnmetAsksAccumulateFromToolInputsAndSurviveALaterProfileUpdate()
    {
        var emptiedProfile = JsonSerializer.SerializeToNode(BeverageAudience)!.AsObject();
        emptiedProfile["requestedStories"] = new JsonArray();
        var withRequest = JsonSerializer.SerializeToNode(BeverageAudience)!.AsObject();
        withRequest["requestedStories"] = new JsonArray("downtime reasons operators type in");
        var model = new ScriptedModel(
            _ => Reply("tool_use", Call(InterviewTools.RecordAudience, withRequest)),
            _ => Reply("tool_use", Call(InterviewTools.FindStorylines, new
            {
                keywords = new[] { "golden batch" },
                askedFor = new object[]
                {
                    new { storyline = "mcc-trip-impact", words = "MCC trip impact for the bottling hall" },
                    new { storyline = "labeler-jams-late-orders", words = "late orders" },         // available: not an unmet ask
                    new { storyline = InterviewTools.NoStoryline, words = "forklift telematics" },
                    new { storyline = "mcc-trip-impact", words = "MCC trip again" }   // same storyline: recorded once
                }
            })),
            // The model updates the profile and forgets requestedStories: nothing recorded is lost.
            _ => Reply("tool_use", Call(InterviewTools.RecordAudience, emptiedProfile)),
            _ => Reply("end_turn", Text("Noted.")));
        var session = Start(model);
        await session.Send("Bottler; they'd love an MCC trip impact view of the bottling hall, late orders, and forklift telematics.");

        var requests = InterviewTools.Requests(session.State).Select(r => r!.AsObject()).ToList();
        Assert.Equal(["downtime reasons operators type in", "MCC trip impact for the bottling hall", "forklift telematics"], requests.Select(r => r["words"]!.GetValue<string>()));
        Assert.Equal("unmapped", requests[0]["engine"]!.GetValue<string>());
        Assert.Equal("later", requests[1]["engine"]!.GetValue<string>());
        Assert.Equal("labeler-jams-late-orders", requests[1]["offeredInstead"]!["id"]!.GetValue<string>());
        Assert.Null(requests[2]["storyline"]);
    }

    [Fact]
    public async Task AskedForRejectsIdsOutsideTheLibrary()
    {
        JsonObject? result = null;
        var model = new ScriptedModel(
            _ => Reply("tool_use", Call(InterviewTools.RecordAudience, BeverageAudience),
                Call(InterviewTools.FindStorylines, new { keywords = Array.Empty<string>(), askedFor = new[] { new { storyline = "made-up", words = "x" } } })),
            r => { result = ResultJson(LastResults(r)[1]); return Reply("end_turn", Text("OK.")); });
        await Start(model).Send("Hello");
        Assert.Equal("$.askedFor[0].storyline", result!["errors"]![0]!["path"]!.GetValue<string>());
    }

    // ---------------- live, through the Pulse Gage proxy ----------------

    /// <summary>
    /// Gated by FLOWSIM_TEST_GAGE=1 plus the Pulse client settings
    /// (TIMEBASE_PULSE_URL, TIMEBASE_CLIENT_ID, TIMEBASE_CLIENT_SECRET,
    /// TIMEBASE_CA_BUNDLE). A 3-turn scripted user reaches a valid preview;
    /// the token usage is written to the test output.
    /// </summary>
    [Fact]
    public async Task LiveInterviewThroughGageReachesAValidPreview()
    {
        if (Environment.GetEnvironmentVariable("FLOWSIM_TEST_GAGE") != "1") return;
        using var gage = new GageClient(GageSettings.FromEnvironment());
        var session = InterviewSession.Start(Library(), new GageInterviewModel(gage), new InMemoryInterviewStore());
        string[] user =
        [
            "I'm preparing a demo for the maintenance manager and the plant manager at a soft-drink bottler with two bottling lines. Their most expensive problem is late orders: when the labeler jams, orders ship late. They run SAP PM and Ignition. Six days of history is fine, and nobody is worried about AI.",
            "The labeler jam storyline sounds right. Call the simulation PlanTestBottler and keep the default line, product and people names.",
            "Yes, compose the model and show me the preview."
        ];
        foreach (var text in user)
        {
            var turn = await session.Send(text, e => output.WriteLine($"{e.Kind}: {e.Text}"));
            Assert.NotEqual(InterviewOutcome.Stopped, turn.Outcome);
        }
        output.WriteLine($"Usage: {session.TotalUsage}");
        Assert.NotNull(session.State.DraftModelJson);
        Assert.True(PlantModelLoader.Load(session.State.DraftModelJson!, DateTimeOffset.UtcNow).IsValid);
        Assert.NotNull(session.State.PreviewJson);
    }

    // ---------------- Postgres store ----------------

    private static string? Connection => Environment.GetEnvironmentVariable("FLOWSIM_TEST_PG");

    [Fact]
    public async Task PostgresStoreKeepsAnAppendOnlyHistoryAndResumes()
    {
        if (Connection is null) return; // database tests run only with FLOWSIM_TEST_PG
        using var store = new PostgresInterviewStore(Connection);
        var model = new ScriptedModel(
            _ => Reply("tool_use", Call(InterviewTools.RecordAudience, BeverageAudience)),
            _ => Reply("end_turn", Text("Noted.")));
        var session = Start(model, store);
        try
        {
            await session.Send("A bottler.");
            var record = store.Load(session.Id)!;
            Assert.Equal(InterviewStatus.Open, record.Status);
            Assert.Equal([1, 2, 3, 4], record.Messages.Select(m => m.Sequence));
            Assert.Equal(["user", "assistant", "user", "assistant"], record.Messages.Select(m => m.Role));
            Assert.Equal(new InterviewUsage(1000, 100, 400, 0), record.Messages[1].Usage);
            Assert.Contains("food-beverage", record.NeedsJson);

            // A new process replays exactly the stored bytes.
            using var second = new PostgresInterviewStore(Connection);
            var next = new ScriptedModel(r =>
            {
                Assert.Equal(record.Messages.Select(m => m.ContentJson), r.Messages.Take(4).Select(m => JsonSerializer.Serialize(m)));
                return Reply("end_turn", Text("Hello again."));
            });
            var resumed = InterviewSession.Resume(session.Id, Library(), next, second, clock: () => Now);
            Assert.Equal(InterviewOutcome.Completed, (await resumed.Send("Back again.")).Outcome);
            Assert.Equal(6, second.Load(session.Id)!.Messages.Count);
            Assert.Equal(new InterviewUsage(3000, 300, 1200, 0), resumed.TotalUsage); // three replies in all
        }
        finally
        {
            // Remove only this test's interview (messages cascade).
            using var db = new NpgsqlConnection(Connection);
            db.Open();
            using var delete = new NpgsqlCommand("DELETE FROM flowsim_interviews WHERE id = @i", db);
            delete.Parameters.AddWithValue("i", session.Id);
            delete.ExecuteNonQuery();
        }
    }
}
