using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Messages;
using IndustrialDataSim.Core.Plant;
using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Interview;

/// <summary>
/// What the interview has produced so far. Persisted on the interview row
/// after every tool call that changes it.
/// </summary>
public sealed class InterviewState
{
    /// <summary>
    /// The needs profile: {"audience": record_audience input, "chosenStorylines":
    /// [...], "requests": [...]}. "requests" accumulates the asks the simulator
    /// cannot show yet ({words, storyline, title, engine, offeredInstead}),
    /// recorded by code from find_storylines askedFor and record_audience
    /// requestedStories; entries are never removed. It feeds the demo guide.
    /// </summary>
    public JsonObject Needs { get; set; } = new();
    public string? DraftModelJson { get; set; }
    public string? PreviewJson { get; set; }
    /// <summary>
    /// Set when this interview already built a plant (it was approved): every
    /// new draft must keep this simulation name, because approving it rebuilds
    /// that plant in place.
    /// </summary>
    public string? LockedSimulation { get; set; }

    /// <summary>Needs key: the hash of the draft that was last approved, so an unchanged plan is not offered for approval again.</summary>
    public const string ApprovedDraftKey = "approvedDraft";

    public static string DraftHash(string draftJson) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(draftJson)));
}

/// <summary>The result of one tool call, returned to the model as a tool_result block.</summary>
public sealed record ToolOutcome(string Content, bool IsError)
{
    public bool NeedsChanged { get; init; }
    public bool DraftChanged { get; init; }
    public bool PreviewChanged { get; init; }
}

/// <summary>
/// The interview's four tools. Each has a strict JSON schema; its input is
/// validated again in code and its problems come back as an is_error tool
/// result with field paths, never as an exception.
///
/// There is deliberately no build or run tool: building and running start
/// only from the user's Approve click in the page (phase 7). A test asserts
/// that no tool name or description offers either.
/// </summary>
public sealed class InterviewTools(InterviewLibrary library, Func<DateTimeOffset> clock, IPlantDirectory? plants = null)
{
    public const string RecordAudience = "record_audience";
    public const string FindStorylines = "find_storylines";
    public const string ComposeModel = "compose_model";
    public const string PreviewPlant = "preview_plant";
    // Existing plants (only with a plant directory, that is in the web container).
    public const string ListPlants = "list_plants";
    public const string DescribePlant = "describe_plant";
    public const string ProposePlantAction = "propose_plant_action";
    public const string ChangePlant = "change_plant";

    /// <summary>Needs keys the plant tools set for the page: a stop or start to confirm, and a move to another interview.</summary>
    public const string PendingActionKey = "pendingAction", HandoffKey = "handoff";

    public const int MaxStorylinesPerPlant = 3;
    public const int DefaultPreviewHours = 24, MaxPreviewHours = 72;
    /// <summary>Tag samples in the stored preview are thinned to this grid to bound its size (about 2,000 points per tag for a week).</summary>
    public const int PreviewSampleSeconds = 300;
    private const int MaxRanked = 8, MaxListedEvents = 8, MaxReportedErrors = 20;

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Activity labels for the page while a tool runs.</summary>
    public static string Activity(string toolName) => toolName switch
    {
        RecordAudience => "Saving the audience profile…",
        FindStorylines => "Matching storylines to the audience…",
        ComposeModel => "Checking the plant model…",
        PreviewPlant => "Running the plant preview…",
        ListPlants => "Looking up your plants…",
        DescribePlant => "Reading the plant's details…",
        ProposePlantAction => "Preparing the request for you to confirm…",
        ChangePlant => "Opening the plant's own interview…",
        _ => "Working…"
    };

    // ---------------- definitions ----------------

    /// <summary>
    /// The tool definitions sent with every request. Schemas use only the
    /// features strict mode supports everywhere: objects with every field
    /// required and additionalProperties false, strings, integers, enums and
    /// arrays. Ranges and counts are enforced in code instead.
    /// </summary>
    public IReadOnlyList<ToolUnion> Definitions => definitions ??= BuildDefinitions(library.Storylines.Storylines.Select(st => st.Id).Append(NoStoryline).ToList(), plants is not null);
    private IReadOnlyList<ToolUnion>? definitions;

    /// <summary>The askedFor value for a request that no library storyline covers.</summary>
    public const string NoStoryline = "none";

    public static IReadOnlyList<string> Names { get; } = [RecordAudience, FindStorylines, ComposeModel, PreviewPlant];
    public static IReadOnlyList<string> PlantToolNames { get; } = [ListPlants, DescribePlant, ProposePlantAction, ChangePlant];

    private static IReadOnlyList<ToolUnion> BuildDefinitions(IReadOnlyList<string> storylineIds, bool withPlants)
    {
        IReadOnlyList<string> StorylineIds() => storylineIds;
        JsonObject StringList(string description) => new() { ["type"] = "array", ["description"] = description, ["items"] = new JsonObject { ["type"] = "string" } };
        JsonObject Enum(IEnumerable<string> values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()) };
        JsonObject Rename(string key, string value, string description) => new()
        {
            ["type"] = "array", ["description"] = description,
            ["items"] = Schema(new() { [key] = new JsonObject { ["type"] = "string" }, [value] = new JsonObject { ["type"] = "string" } })
        };

        var tools = new[]
        {
            Tool(RecordAudience,
                "Save the audience's needs profile: who will watch the demo and what costs them the most. Call it as soon as you know the industry, roles and main problems, and again whenever they change (it replaces the previous profile). Map the user's words to the library vocabulary in the system prompt. Every field is required; use an empty list or string when unknown.",
                new()
                {
                    ["industry"] = Enum(PlantStorylines.Industries),
                    ["product"] = new JsonObject { ["type"] = "string", ["description"] = "What they make and how, for example \"soft drinks on two bottling lines\"." },
                    ["roles"] = new JsonObject { ["type"] = "array", ["description"] = "Roles in the room (at least one).", ["items"] = Enum(PlantStorylines.Roles) },
                    ["kpis"] = StringList("Their KPIs, in the library's KPI words where one fits (for example \"on-time delivery\" for late orders)."),
                    ["pains"] = StringList("The one or two problems that cost them the most, each with a recent example if given."),
                    ["systems"] = StringList("ERP, CMMS, MES, LIMS, historian and SCADA they run, for example \"SAP PM\", \"Ignition\"."),
                    ["constraints"] = StringList("AI, cloud, network or timing constraints."),
                    ["historyDays"] = new JsonObject { ["type"] = "integer", ["description"] = "Days of history the demo needs, 1 to 30 (6 is a safe default for a 7-day purge)." },
                    ["requestedStories"] = StringList("Stories the user asked for that the simulator cannot show yet, in their words.")
                }),
            Tool(FindStorylines,
                "Rank the storyline library for the recorded audience (call record_audience first). The ranking is deterministic: industry, then roles, then KPIs, then your keywords. Each result has its engine status, the reasons, its evidence count and, for storylines that are not available yet, what to offer instead.",
                new()
                {
                    ["keywords"] = StringList("The user's own words for what they want to see, for example \"late orders\" or \"energy spikes\". Empty to rank by the profile alone."),
                    ["askedFor"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Every story the user has explicitly asked to see so far in this interview (not stories you suggested), each mapped to the library storyline that would show it, or \"none\" when no storyline in the library covers it. Repeat earlier asks on every call. Empty list when the user has asked for nothing specific.",
                        ["items"] = Schema(new()
                        {
                            ["storyline"] = Enum(StorylineIds()),
                            ["words"] = new JsonObject { ["type"] = "string", ["description"] = "What the user asked for, in their words." }
                        })
                    }
                }),
            Tool(ComposeModel,
                "Compose and validate the draft plant model from 1 to 3 available storylines that share a base plant, plus the user's names. Returns \"valid\" with a short model summary, or the validation errors with their field paths; fix every error and call again. A valid model is saved as the draft and replaces any earlier draft and preview.",
                new()
                {
                    ["storylines"] = StringList("Ids of the chosen available storylines, in the order to show them."),
                    ["simulation"] = new JsonObject { ["type"] = "string", ["description"] = "PascalCase simulation name, 2–32 letters or digits, for example AcmeNuggs." },
                    ["backfill"] = new JsonObject { ["type"] = "string", ["description"] = "Days of history, for example \"6d\". Empty string keeps the base plant's default." },
                    ["lineNames"] = Rename("id", "name", "Renamed lines by line id (L1, L2). Empty list keeps the base names."),
                    ["productNames"] = Rename("code", "description", "Renamed products by SKU code. Empty list keeps the base names."),
                    ["peopleNames"] = Rename("id", "name", "Renamed operators and technicians by employee id. Empty list keeps the base names.")
                }),
            Tool(PreviewPlant,
                "Run the simulation engine offline over the draft model's backfill plus the given hours after now. Returns the headline numbers and where each storyline appears (times, tickets, holds, uncertified assignments) so you can explain it. Nothing is written to Postgres or Historian. The full preview is saved for the page.",
                new() { ["hours"] = new JsonObject { ["type"] = "integer", ["description"] = $"Hours after now to include, 1 to {MaxPreviewHours} ({DefaultPreviewHours} is the default)." } })
        };
        if (withPlants)
            tools =
            [
                .. tools,
                Tool(ListPlants,
                    "List the plants this page already built, with their status (live, backfilling, stopped …), storylines, history window and Dataset. Call it when the user mentions an existing plant, asks what exists, or asks how a plant is doing. Plants made by other tools are not listed and cannot be changed here.",
                    new()),
                Tool(DescribePlant,
                    "Details of one plant this page built: lines and machines, storylines, tag root, table names, Dataset, history window and how far delivery has got. Read-only.",
                    new() { ["plant"] = new JsonObject { ["type"] = "string", ["description"] = "The simulation name, exactly as list_plants gives it." } }),
                Tool(ProposePlantAction,
                    "Ask the user to stop or start (resume) one of their plants. Nothing happens until the user confirms in the bar at the bottom of the page. Use it only when the user asks for it.",
                    new()
                    {
                        ["plant"] = new JsonObject { ["type"] = "string", ["description"] = "The simulation name, exactly as list_plants gives it." },
                        ["action"] = Enum(["stop", "start"])
                    }),
                Tool(ChangePlant,
                    "Move the user into the interview that built an existing plant, to change it there (storylines, names, history). That interview has the plant's whole conversation; the page opens it and passes the request on. Call it as soon as the user asks to change a plant, and do not compose anything for that plant here.",
                    new()
                    {
                        ["plant"] = new JsonObject { ["type"] = "string", ["description"] = "The simulation name, exactly as list_plants gives it." },
                        ["request"] = new JsonObject { ["type"] = "string", ["description"] = "What the user wants changed, in their own words." }
                    })
            ];
        return tools.Select(t => JsonSerializer.Deserialize<ToolUnion>(t.ToJsonString())!).ToList();
    }

    private static JsonObject Schema(JsonObject properties) => new()
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["required"] = new JsonArray(properties.Select(p => (JsonNode)p.Key).ToArray()),
        ["additionalProperties"] = false
    };

    private static JsonObject Tool(string name, string description, JsonObject properties) => new()
    {
        ["name"] = name, ["description"] = description, ["strict"] = true, ["input_schema"] = Schema(properties)
    };

    // ---------------- execution ----------------

    /// <summary>
    /// Runs one tool call against the state. Never throws for bad input or
    /// an engine problem: the model gets an is_error result it can act on.
    /// </summary>
    public ToolOutcome Execute(string name, JsonElement input, InterviewState state)
    {
        try
        {
            return name switch
            {
                RecordAudience => Record(input, state),
                FindStorylines => Find(input, state),
                ComposeModel => Compose(input, state),
                PreviewPlant => Preview(input, state),
                ListPlants when plants is not null => Success(new JsonObject { ["plants"] = plants.List() }),
                DescribePlant when plants is not null => Describe(input, plants),
                ProposePlantAction when plants is not null => Propose(input, state, plants),
                ChangePlant when plants is not null => Change(input, state, plants),
                _ => Failure([new("tool.unknown", "$", $"There is no tool named that. The tools are {string.Join(", ", Names)}. Building and running are not tools: the user starts them with the Approve button.")])
            };
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Raw exception text can carry model content or paths; it is not returned.
            return Failure([new("tool.internal_error", "$", "The tool failed unexpectedly. Tell the user the preview service hit an internal problem and suggest retrying; do not retry more than once.")]);
        }
    }

    // ---------------- existing plants ----------------

    private static ToolOutcome Success(JsonObject content, bool needsChanged = false) =>
        new(content.ToJsonString(Compact), false) { NeedsChanged = needsChanged };

    private static (string? Plant, ToolOutcome? Problem) KnownPlant(ToolInput read, IPlantDirectory plants)
    {
        string plant = read.Text("plant", allowEmpty: false, 64);
        if (!read.IsValid) return (null, Failure(read.Errors));
        if (plants.Describe(plant) is null)
        {
            var names = plants.List().Select(p => p?["simulation"]?.GetValue<string>()).OfType<string>().ToList();
            return (null, Failure([new("tool.unknown_plant", "$.plant", names.Count == 0
                ? "This page has not built any plants yet. Offer to design one."
                : $"This page did not build a plant with that name. Its plants are: {string.Join(", ", names)}. Plants made by other tools cannot be seen or changed here.")]));
        }
        return (plant, null);
    }

    private static ToolOutcome Describe(JsonElement input, IPlantDirectory plants)
    {
        var read = new ToolInput(input, ["plant"]);
        var (plant, problem) = KnownPlant(read, plants);
        return problem ?? Success(plants.Describe(plant!)!);
    }

    private static ToolOutcome Propose(JsonElement input, InterviewState state, IPlantDirectory plants)
    {
        var read = new ToolInput(input, ["plant", "action"]);
        string action = read.Text("action", allowEmpty: false, 8);
        var (plant, problem) = KnownPlant(read, plants);
        if (problem is not null) return problem;
        if (action is not ("stop" or "start")) return Failure([new("tool.invalid_action", "$.action", "The action is \"stop\" or \"start\".")]);
        state.Needs[PendingActionKey] = new JsonObject { ["plant"] = plant, ["action"] = action };
        return Success(new JsonObject
        {
            ["proposed"] = true, ["plant"] = plant, ["action"] = action,
            ["next"] = $"A bar at the bottom of the page now asks the user to confirm: {(action == "stop" ? "Stop" : "Start")} {plant}. Nothing happens until they click it. Tell them so in one sentence."
        }, needsChanged: true);
    }

    private static ToolOutcome Change(JsonElement input, InterviewState state, IPlantDirectory plants)
    {
        var read = new ToolInput(input, ["plant", "request"]);
        string request = read.Text("request", allowEmpty: false, 2000);
        var (plant, problem) = KnownPlant(read, plants);
        if (problem is not null) return problem;
        if (state.LockedSimulation == plant)
            return Failure([new("tool.same_plant", "$.plant", $"This interview already is {plant}'s interview. Make the change here: compose_model with simulation \"{plant}\", then preview_plant.")]);
        if (plants.InterviewOf(plant!) is not { } interview)
            return Failure([new("tool.no_interview", "$.plant", $"The interview that built {plant} is no longer stored, so it cannot be changed here. Offer to design a new plant instead.")]);
        state.Needs[HandoffKey] = new JsonObject { ["plant"] = plant, ["interviewId"] = interview.ToString("D"), ["request"] = request };
        return Success(new JsonObject
        {
            ["handedOff"] = true, ["plant"] = plant,
            ["next"] = $"The page now opens {plant}'s own interview, with its history, and passes the request there. Say so in one short sentence and stop; do not compose or preview anything here."
        }, needsChanged: true);
    }

    private ToolOutcome Record(JsonElement input, InterviewState state)
    {
        var read = new ToolInput(input, ["industry", "product", "roles", "kpis", "pains", "systems", "constraints", "historyDays", "requestedStories"]);
        string industry = read.Text("industry", allowEmpty: false);
        if (industry.Length > 0 && !PlantStorylines.Industries.Contains(industry))
            read.Errors.Add(new("tool.value_not_allowed", "$.industry", $"Use one of: {string.Join(", ", PlantStorylines.Industries)}."));
        string product = read.Text("product", allowEmpty: true);
        var roles = read.Strings("roles", minimum: 1, allowed: PlantStorylines.Roles);
        var kpis = read.Strings("kpis", 0);
        var pains = read.Strings("pains", 0);
        var systems = read.Strings("systems", 0);
        var constraints = read.Strings("constraints", 0);
        int historyDays = read.Integer("historyDays", 1, 30);
        var requested = read.Strings("requestedStories", 0);
        if (!read.IsValid) return Failure(read.Errors);

        JsonArray Array(IEnumerable<string> items) => new(items.Select(i => (JsonNode)i).ToArray());
        state.Needs["audience"] = new JsonObject
        {
            ["industry"] = industry, ["product"] = product, ["roles"] = Array(roles), ["kpis"] = Array(kpis), ["pains"] = Array(pains),
            ["systems"] = Array(systems), ["constraints"] = Array(constraints), ["historyDays"] = historyDays, ["requestedStories"] = Array(requested)
        };
        // requestedStories only ever adds: a later profile update that leaves it
        // empty must not erase what the user asked for earlier.
        foreach (string words in requested) RecordRequest(state, words, null);
        return new ToolOutcome(Json(new JsonObject { ["saved"] = true, ["next"] = "Call find_storylines to rank the library for this audience." }), false) { NeedsChanged = true };
    }

    private ToolOutcome Find(JsonElement input, InterviewState state)
    {
        var read = new ToolInput(input, ["keywords", "askedFor"]);
        var keywords = read.Strings("keywords", 0);
        var asks = read.Asks("askedFor", library.Storylines.Storylines.Select(s => s.Id).Append(NoStoryline).ToList());
        if (!read.IsValid) return Failure(read.Errors);
        if (Profile(state) is not { } profile)
            return Failure([new("tool.audience_missing", "$", "Record the audience first: call record_audience with the industry and roles, then call find_storylines again.")]);

        // Asks the simulator cannot show are recorded here, by code, from the
        // model's own mapping: the demo guide lists them under "Asked for, not
        // in the simulator yet". No keyword matching is involved.
        bool requestsChanged = false;
        foreach (var (storylineId, words, _) in asks)
        {
            var storyline = library.Storylines.Find(storylineId);
            if (storyline is { Available: true }) continue;
            requestsChanged |= RecordRequest(state, words, storyline);
        }

        var ranked = StorylineRanking.Rank(library.Storylines, profile, keywords).Take(MaxRanked).ToList();
        var results = new JsonArray();
        foreach (var r in ranked)
        {
            var item = new JsonObject
            {
                ["id"] = r.Storyline.Id, ["title"] = r.Storyline.Title, ["engine"] = r.Storyline.Engine, ["available"] = r.Storyline.Available,
                ["score"] = r.Score, ["reasons"] = new JsonArray(r.Reasons.Select(x => (JsonNode)x).ToArray()),
                ["evidence"] = $"raised in {r.Storyline.Evidence} recorded customer meetings", ["pitch"] = r.Storyline.Pitch,
                ["bases"] = new JsonArray(r.Storyline.Bases.Select(x => (JsonNode)x).ToArray()), ["requires"] = new JsonArray(r.Storyline.Requires.Select(x => (JsonNode)x).ToArray())
            };
            if (r.OfferInstead is { } offer)
                item["offerInstead"] = new JsonObject { ["id"] = offer.Id, ["title"] = offer.Title, ["reason"] = offer.Reason };
            results.Add(item);
        }
        var output = new JsonObject
        {
            ["storylines"] = results,
            ["note"] = ranked.Count == 0
                ? "Nothing in the library matches this profile. Ask more about the audience's problems, or offer the most evidenced available storyline."
                : "Propose one to three available storylines. If the user asked for a storyline that is not available, say plainly it is not in the simulator yet and offer its offerInstead."
        };
        var notAvailable = Requests(state);
        if (notAvailable.Count > 0)
        {
            output["recordedRequests"] = notAvailable.DeepClone();
            output["requestNote"] = "These asks are saved for the demo guide as \"not in the simulator yet\". Tell the user so in plain words, and offer each one's offeredInstead when it has one.";
        }
        return new ToolOutcome(Json(output), false) { NeedsChanged = requestsChanged };
    }

    private ToolOutcome Compose(JsonElement input, InterviewState state)
    {
        var read = new ToolInput(input, ["storylines", "simulation", "backfill", "lineNames", "productNames", "peopleNames"]);
        var ids = read.Strings("storylines", 1, MaxStorylinesPerPlant);
        string simulation = read.Text("simulation", allowEmpty: false, 64);
        string backfill = read.Text("backfill", allowEmpty: true, 16);
        var lineNames = read.Pairs("lineNames", "id", "name", 2);
        var productNames = read.Pairs("productNames", "code", "description", 6);
        var peopleNames = read.Pairs("peopleNames", "id", "name", 12);
        if (!read.IsValid) return Failure(read.Errors);

        // Storylines: known, available, one base.
        var errors = new List<ValidationError>();
        var chosen = new List<PlantStoryline>();
        for (int i = 0; i < ids.Count; i++)
        {
            string path = $"$.storylines[{i}]";
            if (library.Storylines.Find(ids[i]) is not { } storyline)
                errors.Add(new("tool.unknown_storyline", path, $"No storyline has this id. Available ids: {string.Join(", ", library.Storylines.Storylines.Where(s => s.Available).Select(s => s.Id))}."));
            else if (!storyline.Available)
                errors.Add(new("tool.storyline_not_available", path, $"\"{storyline.Title}\" is {storyline.Engine}, not in the simulator yet." +
                    (storyline.Closest is { } closest ? $" Offer \"{closest}\" instead." : " Nothing close is available; tell the user plainly.")));
            else if (chosen.Count > 0 && PlantStorylines.CommonBase([.. chosen, storyline]) is null)
                errors.Add(new("tool.storyline_base", path, $"This storyline runs on the base plant(s) {string.Join(", ", storyline.Bases)}, but the storylines before it share only {string.Join(", ", chosen[0].Bases.Where(b => chosen.All(c => c.RunsOn(b))))}. Choose storylines that share a base."));
            else chosen.Add(storyline);
        }
        if (errors.Count > 0) return Failure(errors);
        string baseId = PlantStorylines.CommonBase(chosen)!;
        if (!library.Bases.TryGetValue(baseId, out var baseJson))
            return Failure([new("tool.base_missing", "$.storylines", $"The base plant \"{baseId}\" is missing from the storyline folder. Tell the user the library is incomplete.")]);

        if (state.LockedSimulation is { } locked && !string.Equals(simulation, locked, StringComparison.Ordinal))
            return Failure([new("tool.simulation_locked", "$.simulation", $"This interview changes the plant {locked}, which is running. Keep simulation \"{locked}\": approving the new draft rebuilds {locked} in place.")]);
        var overrides = new JsonObject { ["simulation"] = simulation };
        if (backfill.Length > 0) overrides["timeline"] = new JsonObject { ["backfill"] = backfill };
        var composed = PlantStorylines.Compose(baseId, baseJson, chosen, overrides);
        if (!composed.IsValid)
            return Failure(composed.Errors.Select(e => e.Code == "storyline.wrong_base" || e.Code == "storyline.not_available"
                ? e with { Path = $"$.storylines[{ids.IndexOf(e.Path)}]" } : e).ToList());

        var model = JsonNode.Parse(composed.ModelJson!)!.AsObject();
        ApplyRenames(model, lineNames, productNames, peopleNames, errors);
        if (errors.Count > 0) return Failure(errors);

        string modelJson = model.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var loaded = PlantModelLoader.Load(modelJson, Anchor());
        if (!loaded.IsValid)
        {
            // Loader paths are model paths ($.simulation, $.timeline.backfill …); say how they map to this tool's fields.
            var fieldErrors = loaded.Errors.Select(e => e with { Path = "model " + e.Path }).ToList();
            return Failure(fieldErrors, "The composed model is invalid. Paths starting with \"model $.\" are plant model fields: $.simulation is the simulation field, $.timeline.backfill is backfill, $.lines[i].name comes from lineNames, $.skus[i].description from productNames, $.people.* from peopleNames.");
        }

        var plant = loaded.Model!;
        // A storyline that sets its own backfill needs all of it (for example
        // 5a's 14 days put the jacket clean, and the recovery after it, inside
        // the history). A shorter override would quietly lose that part.
        var own = chosen.Select(c => c.FragmentFor(baseId)?["timeline"]?["backfill"]?.GetValue<string>()).OfType<string>().ToList();
        if (own.Count > 0 && backfill.Length > 0)
        {
            var storylineDefault = PlantStorylines.Compose(baseId, baseJson, chosen, new JsonObject { ["simulation"] = simulation });
            var defaultModel = storylineDefault.IsValid ? PlantModelLoader.Load(storylineDefault.ModelJson!, Anchor()).Model : null;
            if (defaultModel is not null && plant.StartUtc > defaultModel.StartUtc)
                return Failure([new("tool.backfill_too_short", "$.backfill",
                    $"These storylines need their own history length ({string.Join(", ", own.Distinct())}): their events, and the recoveries after them, are timed for it. " +
                    "Leave backfill empty to use it.")]);
        }
        // Every planted event needs a normal day of history before it: a drift
        // that starts before the history has no golden baseline to compare
        // with, and the plant starts in the middle of the problem.
        var planted = plant.Drifts.Select(d => d.FromUtc)
            .Concat(plant.Quality?.Excursions.Select(e => e.FromUtc) ?? [])
            .Concat(plant.Incidents.Select(i => i.AtUtc)).ToList();
        if (planted.Count > 0 && planted.Min() < plant.StartUtc.AddDays(1))
        {
            int days = (int)Math.Ceiling((Anchor() - planted.Min()).TotalDays) + 1;
            return Failure([new("tool.backfill_too_short", "$.backfill",
                $"The chosen storylines plant events from {planted.Min():yyyy-MM-dd HH:mm} UTC, but the history starts at {plant.StartUtc:yyyy-MM-dd HH:mm} UTC. " +
                $"Use a backfill of at least {days}d (or leave backfill empty for the storylines' own).")]);
        }
        state.DraftModelJson = modelJson;
        state.PreviewJson = null; // a preview of an older draft must never be approved
        state.Needs["chosenStorylines"] = new JsonArray(chosen.Select(c => (JsonNode)c.Id).ToArray());
        var summary = new JsonObject
        {
            ["valid"] = true,
            ["simulation"] = plant.Simulation,
            ["tablePrefix"] = plant.TablePrefix,
            ["tagRoot"] = plant.TagRoot,
            ["base"] = baseId,
            ["storylines"] = new JsonArray(chosen.Select(c => (JsonNode)c.Id).ToArray()),
            ["historyStartsUtc"] = plant.StartUtc,
            ["lines"] = new JsonArray(plant.Lines.Select(l => (JsonNode)new JsonObject { ["id"] = l.Id, ["name"] = l.Name, ["equipment"] = l.Equipment.Count }).ToArray()),
            ["products"] = new JsonArray(plant.Skus.Select(s => (JsonNode)new JsonObject { ["code"] = s.Code, ["description"] = s.Description }).ToArray()),
            ["operators"] = new JsonArray(plant.Operators.Select(p => (JsonNode)new JsonObject { ["id"] = p.Id, ["name"] = p.Name, ["shift"] = p.Shift, ["line"] = p.Line }).ToArray()),
            ["technicians"] = new JsonArray(plant.Technicians.Select(p => (JsonNode)new JsonObject { ["id"] = p.Id, ["name"] = p.Name, ["shift"] = p.Shift }).ToArray()),
            ["areas"] = new JsonArray(new[] { "production", "maintenance" }.Concat(plant.Quality is null ? [] : ["quality"]).Concat(plant.Shipping is null ? [] : ["shipping"])
                .Concat(plant.Maintenance.Certification is null ? [] : ["certification"]).Select(a => (JsonNode)a).ToArray()),
            ["next"] = "Call preview_plant to run the engine over this draft."
        };
        return new ToolOutcome(Json(summary), false) { NeedsChanged = true, DraftChanged = true, PreviewChanged = true };
    }

    /// <summary>Applies the interview's renames by id; an unknown id is an error naming the valid ones.</summary>
    private static void ApplyRenames(JsonObject model, List<(string Key, string Value, string Path)> lineNames,
        List<(string Key, string Value, string Path)> productNames, List<(string Key, string Value, string Path)> peopleNames, List<ValidationError> errors)
    {
        void Rename(IEnumerable<JsonObject> items, string idField, string nameField, List<(string Key, string Value, string Path)> renames, string field, string what)
        {
            var list = items.ToList();
            foreach (var (key, value, path) in renames)
            {
                var target = list.FirstOrDefault(i => i[idField]?.GetValue<string>() == key);
                if (target is null)
                    errors.Add(new("tool.unknown_id", path + "." + field, $"No {what} has this {field}. Valid values: {string.Join(", ", list.Select(i => i[idField]?.GetValue<string>()))}."));
                else target[nameField] = value;
            }
        }
        Rename(Objects(model["lines"]), "id", "name", lineNames, "id", "line");
        Rename(Objects(model["skus"]), "code", "description", productNames, "code", "product");
        var people = model["people"] as JsonObject;
        Rename(Objects(people?["operators"]).Concat(Objects(people?["technicians"])), "id", "name", peopleNames, "id", "person");
    }

    private static IEnumerable<JsonObject> Objects(JsonNode? node) => node is JsonArray array ? array.OfType<JsonObject>() : [];

    private ToolOutcome Preview(JsonElement input, InterviewState state)
    {
        var read = new ToolInput(input, ["hours"]);
        int hours = read.Integer("hours", 1, MaxPreviewHours);
        if (!read.IsValid) return Failure(read.Errors);
        if (state.DraftModelJson is null)
            return Failure([new("tool.draft_missing", "$", "There is no valid draft yet. Call compose_model until it returns valid, then call preview_plant.")]);

        var anchor = Anchor();
        var loaded = PlantModelLoader.Load(state.DraftModelJson, anchor);
        if (!loaded.IsValid)
            // A draft valid when composed can expire, for example a certificate date relative to an older "now".
            return Failure(loaded.Errors.Select(e => e with { Path = "model " + e.Path }).ToList(), "The draft no longer validates at the current time. Call compose_model again.");
        var model = loaded.Model!;
        var end = model.AnchorUtc.AddHours(hours);
        if (model.EndUtc is { } modelEnd && modelEnd < end) end = modelEnd;
        var run = PlantEngine.Run(model, end);
        // camelCase, like the CLI's plant preview, so the page's shared renderer reads either.
        var document = JsonSerializer.SerializeToNode(PlantPreview.Build(run, PreviewSampleSeconds), CamelCase)!.AsObject();
        state.PreviewJson = document.ToJsonString(Compact);

        var tables = PlantPreview.FinalTables(run);
        var chosen = (state.Needs["chosenStorylines"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>()
            .Select(library.Storylines.Find).OfType<PlantStoryline>().ToList();
        var output = new JsonObject
        {
            ["simulation"] = model.Simulation,
            ["anchorUtc"] = model.AnchorUtc,
            ["startUtc"] = run.StartUtc,
            ["endUtc"] = run.EndUtc,
            ["summary"] = document["summary"]?.DeepClone(),
            ["rows"] = new JsonObject(tables.Select(t => KeyValuePair.Create(model.TablePrefix + t.Key, (JsonNode?)t.Value.Count))),
            ["storylines"] = new JsonArray(chosen.Select(s => (JsonNode)Appearances(s, model, run, tables, document, PlantStorylines.CommonBase(chosen))).ToArray()),
            ["next"] = "Explain the numbers and each storyline's moments, then ask the user to click \"Review plan\" in the bar at the bottom, check the plan, and click \"Approve plan\", or say what to change. You cannot build or run the plant yourself."
        };
        return new ToolOutcome(Json(output), false) { PreviewChanged = true };
    }

    /// <summary>
    /// Where one storyline shows up in a run, from what its fragment plants.
    /// Public for the demo guide, which reruns the admitted plant.
    /// <paramref name="document"/> is the camelCase preview document of the run.
    /// </summary>
    public static JsonObject Appearances(PlantStoryline storyline, PlantModel model, PlantRun run,
        Dictionary<string, List<Dictionary<string, object?>>> tables, JsonObject document, string? baseId = null)
    {
        List<Dictionary<string, object?>> Rows(string name) => tables.TryGetValue(name, out var rows) ? rows : [];
        var moments = new JsonArray();
        var fragment = storyline.FragmentFor(baseId) ?? storyline.Fragment ?? new JsonObject();

        // Planted breakdowns: the warning build-up, the failure, and the tickets it opened.
        var modes = Objects(fragment["incidents"]).Select(i => i["failureMode"]?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var failure in run.Failures.Where(f => f.Planted && modes.Contains(f.Mode)).OrderBy(f => f.FailedUtc).Take(MaxListedEvents))
        {
            var tickets = Rows("maintenancetickets").Where(t => (string?)t["failure_code"] == failure.Mode && t["created_utc"] is DateTimeOffset created &&
                created >= failure.FailedUtc && created <= failure.FailedUtc.AddHours(2)).Take(3)
                .Select(t => (JsonNode)new JsonObject
                {
                    ["ticket"] = (string?)t["ticket_id"], ["createdUtc"] = Time(t["created_utc"]), ["technician"] = (string?)t["technician_id"],
                    ["assignedUtc"] = Time(t["assigned_utc"]), ["completedUtc"] = Time(t["completed_utc"]), ["status"] = (string?)t["status"]
                }).ToArray();
            moments.Add(new JsonObject
            {
                ["kind"] = "breakdown", ["failureMode"] = failure.Mode, ["line"] = failure.Line, ["equipment"] = failure.Equipment,
                ["warningStartsUtc"] = failure.WarningHours > 0 ? failure.FailedUtc.AddHours(-failure.WarningHours) : null,
                ["failedUtc"] = failure.FailedUtc, ["repairedUtc"] = failure.RepairedUtc, ["tickets"] = new JsonArray(tickets)
            });
        }

        // Planted quality excursions: the window and the holds it caused.
        if (fragment["quality"] is JsonObject quality && quality["excursions"] is JsonArray && model.Quality is { } plantQuality)
            foreach (var excursion in plantQuality.Excursions.Take(MaxListedEvents))
            {
                var holds = Rows("qualityholds").Where(h => h["created_utc"] is DateTimeOffset c && c >= excursion.FromUtc && c <= excursion.UntilUtc.AddDays(1)).ToList();
                moments.Add(new JsonObject
                {
                    ["kind"] = "quality excursion", ["parameter"] = excursion.Parameter, ["fromUtc"] = excursion.FromUtc, ["untilUtc"] = excursion.UntilUtc,
                    ["holds"] = holds.Count,
                    ["firstHolds"] = new JsonArray(holds.Take(MaxListedEvents).Select(h => (JsonNode)new JsonObject
                    {
                        ["hold"] = (string?)h["hold_id"], ["lot"] = (string?)h["lot_id"], ["reason"] = (string?)h["reason"],
                        ["createdUtc"] = Time(h["created_utc"]), ["releasedUtc"] = Time(h["released_utc"]), ["disposition"] = (string?)h["disposition"]
                    }).ToArray())
                });
            }

        // Certification: the uncertified assignments and runs the preview summary already lists.
        var certification = (fragment["maintenance"] as JsonObject)?["certification"] as JsonObject;
        var summary = document["summary"] as JsonObject;
        if (certification?["issued"] is JsonArray && summary?["uncertifiedAssignments"] is JsonArray assignments)
            moments.Add(new JsonObject { ["kind"] = "uncertified assignments", ["count"] = assignments.Count, ["first"] = new JsonArray(assignments.Take(MaxListedEvents).Select(a => a?.DeepClone()).ToArray()) });
        if (certification?["operators"] is JsonObject && summary?["uncertifiedRuns"] is JsonArray runs)
            moments.Add(new JsonObject { ["kind"] = "uncertified runs", ["count"] = runs.Count, ["first"] = new JsonArray(runs.Take(MaxListedEvents).Select(r => r?.DeepClone()).ToArray()) });

        // Demand changes (for example tighter due dates) show in the on-time numbers.
        if (fragment["demand"] is JsonObject)
            moments.Add(new JsonObject { ["kind"] = "demand", ["onTimePercent"] = summary?["onTimePercent"]?.DeepClone(), ["completedOrders"] = summary?["completedOrders"]?.DeepClone() });

        return new JsonObject
        {
            ["id"] = storyline.Id, ["title"] = storyline.Title, ["moments"] = moments,
            ["talkTrack"] = new JsonArray(storyline.TalkTrackFor(baseId).Select(t => (JsonNode)$"{t.Tool}: {t.Ask}").ToArray())
        };
    }

    // ---------------- helpers ----------------

    /// <summary>"Now" for validation and previews, in whole seconds like the CLI, so record times stay tidy and reproducible.</summary>
    private DateTimeOffset Anchor()
    {
        var now = clock();
        return new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }

    /// <summary>The asks the simulator cannot show yet: Needs["requests"], in the order first asked.</summary>
    public static JsonArray Requests(InterviewState state) =>
        state.Needs["requests"] as JsonArray ?? (JsonArray)(state.Needs["requests"] = new JsonArray());

    /// <summary>
    /// Adds one unmet ask unless it is already recorded (same storyline id, or
    /// the same words when it maps to no storyline). Returns true when added.
    /// Entries are never removed: the list is what the user asked for.
    /// </summary>
    private bool RecordRequest(InterviewState state, string words, PlantStoryline? storyline)
    {
        var requests = Requests(state);
        foreach (var existing in requests.OfType<JsonObject>())
        {
            string? id = existing["storyline"]?.GetValue<string>();
            if (storyline is not null && id == storyline.Id) return false;
            if (string.Equals(existing["words"]?.GetValue<string>(), words, StringComparison.OrdinalIgnoreCase))
            {
                // A words-only entry (from record_audience) learns its storyline when find_storylines maps it.
                if (id is null && storyline is not null) Describe(existing, storyline);
                return storyline is not null && id is null;
            }
        }
        var entry = new JsonObject { ["words"] = words, ["storyline"] = null, ["title"] = null, ["engine"] = storyline is null ? "unmapped" : null, ["offeredInstead"] = null };
        if (storyline is not null) Describe(entry, storyline);
        requests.Add(entry);
        return true;

        void Describe(JsonObject target, PlantStoryline s)
        {
            target["storyline"] = s.Id;
            target["title"] = s.Title;
            target["engine"] = s.Engine;
            target["offeredInstead"] = s.Closest is { } closest && library.Storylines.Find(closest) is { } offer
                ? new JsonObject { ["id"] = offer.Id, ["title"] = offer.Title } : null;
        }
    }

    internal static AudienceProfile? Profile(InterviewState state)
    {
        if (state.Needs["audience"] is not JsonObject audience) return null;
        List<string> List(string name) => (audience[name] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>().ToList();
        return new AudienceProfile(audience["industry"]?.GetValue<string>() ?? "", List("roles"), List("kpis"));
    }

    private static string? Time(object? value) => value is DateTimeOffset t ? t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ") : null;

    private static string Json(JsonNode node) => node.ToJsonString(Compact);

    private static ToolOutcome Failure(IReadOnlyList<ValidationError> errors, string? hint = null)
    {
        var output = new JsonObject
        {
            ["valid"] = false,
            ["errors"] = new JsonArray(errors.Take(MaxReportedErrors).Select(e => (JsonNode)new JsonObject { ["code"] = e.Code, ["path"] = e.Path, ["message"] = e.Message }).ToArray())
        };
        if (errors.Count > MaxReportedErrors)
            output["omitted"] = $"{errors.Count - MaxReportedErrors} more errors were not listed. Fix these, then call the tool again to see the rest.";
        if (hint is not null) output["hint"] = hint;
        return new ToolOutcome(Json(output), true);
    }
}
