using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Core.Plant;

/// <summary>One step of a storyline's demo talk track: the tool, what to open, what to ask, what the audience sees.</summary>
public sealed record PlantTalkStep(string Tool, IReadOnlyList<string> Tags, IReadOnlyList<string> Tables, string Ask, string Expect);

/// <summary>
/// A demo storyline grounded in real customer needs: who it is for, what it
/// shows, and how to build it (a model fragment on a base plant). Engine is
/// "available" (buildable today), "planned" (on the roadmap) or "later".
/// </summary>
public sealed record PlantStoryline(string Id, string Title, string Pitch, IReadOnlyList<string> Industries,
    IReadOnlyList<string> Roles, IReadOnlyList<string> Kpis, int Evidence, IReadOnlyList<string> Systems,
    string Engine, IReadOnlyList<string> Requires, string? Closest, string? Base, JsonObject? Fragment, IReadOnlyList<PlantTalkStep> TalkTrack)
{
    public bool Available => Engine == "available";
}

public sealed record PlantStorylineLibrary(IReadOnlyList<PlantStoryline> Storylines)
{
    public PlantStoryline? Find(string id) => Storylines.FirstOrDefault(s => s.Id == id);
}

public sealed record PlantStorylineLoadResult(PlantStorylineLibrary? Library, IReadOnlyList<ValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed record PlantComposeResult(string? ModelJson, IReadOnlyList<ValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// The storyline library (examples/storylines/storylines.json): strict loading,
/// and composition of a plant model from a base, the chosen storylines'
/// fragments and the interview's overrides.
///
/// Composition is a JSON merge patch (RFC 7386) per fragment, with two rules:
/// - the lists of planted events (incidents, quality excursions, certificate
///   issues and renewals, work instructions, golden-batch drifts and the
///   preventive tasks that end them) add up, so
///   several storylines can each plant their own events;
/// - any other value two storylines set differently is a conflict, reported
///   with both storyline ids and the path, never silently overwritten.
/// Overrides (simulation name, seed, people …) are applied last and always win.
/// </summary>
public static class PlantStorylines
{
    public static readonly IReadOnlyList<string> Industries =
        ["food-beverage", "cpg", "packaging", "chemicals", "pharma", "discrete", "metals", "mining", "water-energy"];

    public static readonly IReadOnlyList<string> Roles =
        ["plant-manager", "maintenance", "reliability", "ci-oee", "quality", "process-engineer", "it-ot-architect", "controls", "executive", "ehs"];

    public static readonly IReadOnlyList<string> EngineStates = ["available", "planned", "later"];

    public static readonly IReadOnlyList<string> Tools = ["Explorer", "Connect", "Atlas", "Gage"];

    /// <summary>Areas a storyline can require. Base plants list which they have.</summary>
    public static readonly IReadOnlyList<string> Areas = ["quality", "shipping", "certification", "batch"];

    /// <summary>Lists whose items add up across storylines instead of replacing each other.</summary>
    private static readonly string[] AdditivePaths =
    [
        "$.incidents", "$.quality.excursions", "$.maintenance.certification.issued", "$.maintenance.certification.renewals",
        "$.maintenance.certification.operators.workInstructions", "$.drifts", "$.maintenance.preventive"
    ];

    private static readonly string[] EntryFields =
        ["id", "title", "pitch", "industries", "roles", "kpis", "evidence", "systems", "engine", "requires", "closest", "base", "fragment", "talkTrack"];

    private static readonly string[] StepFields = ["tool", "tags", "tables", "ask", "expect"];

    public static PlantStorylineLoadResult Load(string json)
    {
        var errors = new List<ValidationError>();
        JsonObject root;
        try { root = JsonNode.Parse(json) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { return new(null, [new("storyline.invalid_json", "$", "The storyline library must be a JSON object.")]); }
        Fields(root, ["schemaVersion", "kind", "source", "storylines"], "$", errors);
        if (root["schemaVersion"]?.GetValueKind() != JsonValueKind.Number || root["schemaVersion"]!.GetValue<int>() != 1)
            errors.Add(new("storyline.schema_version", "$.schemaVersion", "Set schemaVersion to 1."));
        if (root["kind"]?.ToString() != "storylines")
            errors.Add(new("storyline.kind", "$.kind", "Set kind to \"storylines\"."));
        var list = new List<PlantStoryline>();
        if (root["storylines"] is not JsonArray items)
            errors.Add(new("storyline.missing", "$.storylines", "Add a storylines array."));
        else
            for (int i = 0; i < items.Count; i++)
                if (Entry(items[i], $"$.storylines[{i}]", errors) is { } entry)
                {
                    if (list.Any(s => s.Id == entry.Id))
                        errors.Add(new("storyline.duplicate_id", $"$.storylines[{i}].id", $"The id \"{entry.Id}\" is already used. Give every storyline a unique id."));
                    list.Add(entry);
                }
        for (int i = 0; i < list.Count; i++)
            if (list[i].Closest is { } closest && list.FirstOrDefault(s => s.Id == closest) is not { Available: true })
                errors.Add(new("storyline.closest", $"$.storylines[{i}].closest", $"\"{closest}\" is not an available storyline. Name an available one, or null."));
        return errors.Count == 0 ? new(new PlantStorylineLibrary(list), []) : new(null, errors);
    }

    private static PlantStoryline? Entry(JsonNode? node, string path, List<ValidationError> errors)
    {
        if (node is not JsonObject item)
        {
            errors.Add(new("storyline.entry", path, "Each storyline must be an object."));
            return null;
        }
        int before = errors.Count;
        Fields(item, EntryFields, path, errors);
        string id = Text(item, "id", path, errors);
        if (id.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z][a-z0-9-]{2,63}$"))
            errors.Add(new("storyline.id", path + ".id", "Use a kebab-case id: lowercase letters, digits and hyphens, starting with a letter."));
        string title = Text(item, "title", path, errors), pitch = Text(item, "pitch", path, errors);
        var industries = Strings(item, "industries", path, errors, Industries, minimum: 1);
        var roles = Strings(item, "roles", path, errors, Roles, minimum: 1);
        var kpis = Strings(item, "kpis", path, errors, null, minimum: 1);
        var systems = Strings(item, "systems", path, errors, null, minimum: 0);
        var requires = Strings(item, "requires", path, errors, Areas, minimum: 0);
        int evidence = item["evidence"]?.GetValueKind() == JsonValueKind.Number ? item["evidence"]!.GetValue<int>() : -1;
        if (evidence < 0) errors.Add(new("storyline.evidence", path + ".evidence", "Set evidence to the number of customer meetings that support this storyline (0 or more)."));
        string engine = Text(item, "engine", path, errors);
        if (engine.Length > 0 && !EngineStates.Contains(engine))
            errors.Add(new("storyline.engine", path + ".engine", "Set engine to available, planned or later."));
        if (!item.ContainsKey("closest"))
            errors.Add(new("storyline.closest", path + ".closest", "Set closest to the id of the available storyline to offer instead (null when none fits, or for available storylines)."));
        string? closest = item["closest"]?.GetValueKind() == JsonValueKind.String ? item["closest"]!.GetValue<string>() : null;
        string? baseId = item["base"]?.GetValueKind() == JsonValueKind.String ? item["base"]!.GetValue<string>() : null;
        var fragment = item["fragment"] as JsonObject;
        var talk = new List<PlantTalkStep>();
        if (item["talkTrack"] is JsonArray steps)
        {
            for (int i = 0; i < steps.Count; i++)
                if (Step(steps[i], $"{path}.talkTrack[{i}]", errors) is { } step) talk.Add(step);
        }
        else
            errors.Add(new("storyline.talk_track", path + ".talkTrack", "Add a talkTrack array (empty for storylines that are not available yet)."));
        if (engine == "available")
        {
            if (baseId is null) errors.Add(new("storyline.base", path + ".base", "An available storyline needs a base plant id."));
            if (fragment is null) errors.Add(new("storyline.fragment", path + ".fragment", "An available storyline needs a fragment object (it may be empty)."));
            if (talk.Count < 3 || talk.Count > 6) errors.Add(new("storyline.talk_track", path + ".talkTrack", "An available storyline needs 3 to 6 talk-track steps."));
        }
        if (errors.Count > before) return null;
        return new(id, title, pitch, industries, roles, kpis, evidence, systems, engine, requires, closest, baseId, fragment, talk);
    }

    private static PlantTalkStep? Step(JsonNode? node, string path, List<ValidationError> errors)
    {
        if (node is not JsonObject step)
        {
            errors.Add(new("storyline.talk_step", path, "Each talk-track step must be an object."));
            return null;
        }
        int before = errors.Count;
        Fields(step, StepFields, path, errors);
        string tool = Text(step, "tool", path, errors);
        if (tool.Length > 0 && !Tools.Contains(tool))
            errors.Add(new("storyline.tool", path + ".tool", "Set tool to Explorer, Connect, Atlas or Gage."));
        var tags = Strings(step, "tags", path, errors, null, minimum: 0);
        var tables = Strings(step, "tables", path, errors, null, minimum: 0);
        string ask = Text(step, "ask", path, errors), expect = Text(step, "expect", path, errors);
        return errors.Count > before ? null : new(tool, tags, tables, ask, expect);
    }

    /// <summary>
    /// Builds plant model JSON: the base, then each storyline's fragment in
    /// order, then the overrides. Returns errors for unavailable storylines,
    /// storylines written for another base, and conflicting fragments.
    /// </summary>
    public static PlantComposeResult Compose(string baseId, string baseJson, IReadOnlyList<PlantStoryline> storylines, JsonObject? overrides = null)
    {
        var errors = new List<ValidationError>();
        if (JsonNode.Parse(baseJson) is not JsonObject model)
            return new(null, [new("storyline.base_json", "$", "The base plant must be a JSON object.")]);
        var owners = new Dictionary<string, (string Storyline, string Value)>(StringComparer.Ordinal);
        foreach (var storyline in storylines)
        {
            if (!storyline.Available)
            {
                errors.Add(new("storyline.not_available", storyline.Id, $"\"{storyline.Title}\" is {storyline.Engine}, not available yet. Choose an available storyline."));
                continue;
            }
            if (storyline.Base != baseId)
            {
                errors.Add(new("storyline.wrong_base", storyline.Id, $"\"{storyline.Title}\" is written for the base \"{storyline.Base}\", not \"{baseId}\"."));
                continue;
            }
            Merge(model, storyline.Fragment!, "$", storyline.Id, owners, errors);
        }
        if (overrides is not null) Merge(model, overrides, "$", null, null, errors);
        return errors.Count == 0 ? new(model.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), []) : new(null, errors);
    }

    private static void Merge(JsonObject target, JsonObject patch, string path, string? storyline,
        Dictionary<string, (string Storyline, string Value)>? owners, List<ValidationError> errors)
    {
        foreach (var (key, value) in patch.ToList())
        {
            string childPath = path + "." + key;
            if (value is JsonObject patchObject)
            {
                if (target[key] is not JsonObject targetObject) target[key] = targetObject = new JsonObject();
                Merge(targetObject, patchObject, childPath, storyline, owners, errors);
                continue;
            }
            if (storyline is not null && AdditivePaths.Contains(childPath) && value is JsonArray additions)
            {
                if (target[key] is not JsonArray existing) target[key] = existing = new JsonArray();
                foreach (var addition in additions) existing.Add(addition?.DeepClone());
                continue;
            }
            if (owners is not null && storyline is not null)
            {
                string text = value?.ToJsonString() ?? "null";
                if (owners.TryGetValue(childPath, out var owner) && owner.Storyline != storyline && owner.Value != text)
                {
                    errors.Add(new("storyline.conflict", childPath, $"\"{owner.Storyline}\" and \"{storyline}\" set {childPath} differently. Choose one of them, or set this value in the overrides."));
                    continue;
                }
                owners[childPath] = (storyline, text);
            }
            if (value is null) target.Remove(key); // RFC 7386: null removes
            else target[key] = value.DeepClone();
        }
    }

    // ---------------- strict-reading helpers ----------------

    private static void Fields(JsonObject item, string[] allowed, string path, List<ValidationError> errors)
    {
        foreach (var (key, _) in item)
            if (!allowed.Contains(key))
                errors.Add(new("storyline.unknown_field", $"{path}.{key}", $"Remove \"{key}\"; allowed fields are {string.Join(", ", allowed)}."));
    }

    private static string Text(JsonObject item, string name, string path, List<ValidationError> errors)
    {
        if (item[name]?.GetValueKind() == JsonValueKind.String && item[name]!.GetValue<string>().Trim().Length > 0)
            return item[name]!.GetValue<string>();
        errors.Add(new("storyline.missing_text", $"{path}.{name}", $"Set {name} to a non-empty string."));
        return "";
    }

    private static List<string> Strings(JsonObject item, string name, string path, List<ValidationError> errors, IReadOnlyList<string>? allowed, int minimum)
    {
        var result = new List<string>();
        if (item[name] is not JsonArray array)
        {
            errors.Add(new("storyline.missing_list", $"{path}.{name}", $"Set {name} to a list of strings."));
            return result;
        }
        for (int i = 0; i < array.Count; i++)
        {
            string? value = array[i]?.GetValueKind() == JsonValueKind.String ? array[i]!.GetValue<string>() : null;
            if (value is null || (allowed is not null && !allowed.Contains(value)))
                errors.Add(new("storyline.list_value", $"{path}.{name}[{i}]",
                    allowed is null ? "Use a non-empty string." : $"Use one of: {string.Join(", ", allowed)}."));
            else result.Add(value);
        }
        if (result.Count < minimum) errors.Add(new("storyline.list_empty", $"{path}.{name}", $"List at least {minimum} value(s) in {name}."));
        return result;
    }
}
