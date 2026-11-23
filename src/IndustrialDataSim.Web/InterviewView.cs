using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Plant;
using IndustrialDataSim.Interview;

namespace IndustrialDataSim.Web;

/// <summary>
/// What the page shows for an interview: the conversation as chat items, the
/// storyline cards, the draft and the confirmation summary for Approve.
/// Built from the stored record; nothing here calls the model.
/// </summary>
public static class InterviewView
{
    /// <param name="ownDatasets">True when approving creates a Dataset named for the plant; false (no Historian) shows <paramref name="sharedDataset"/>.</param>
    public static object Build(InterviewRecord record, InterviewLibrary library, bool ownDatasets, string? sharedDataset, bool busy, DateTimeOffset nowUtc)
    {
        var needs = record.NeedsJson is null ? new JsonObject() : JsonNode.Parse(record.NeedsJson) as JsonObject ?? new JsonObject();
        return new
        {
            id = record.Id,
            status = record.Status,
            busy,
            approvedSimulation = record.ApprovedSimulation,
            messages = Messages(record),
            needs = new { audience = needs["audience"], requests = needs["requests"] ?? new JsonArray() },
            storylines = Cards(needs, library),
            approval = Approval(record, needs, ownDatasets, sharedDataset, nowUtc),
            // Set by the existing-plant tools: a stop or start to confirm, and a move into a plant's interview.
            pendingAction = needs[InterviewTools.PendingActionKey],
            handoff = needs[InterviewTools.HandoffKey],
            // The stored preview document as written by preview_plant (camelCase, like plant preview).
            preview = record.PreviewJson is null ? null : JsonNode.Parse(record.PreviewJson)
        };
    }

    /// <summary>
    /// The conversation as the user saw it: typed messages, the interviewer's
    /// text, a chip per tool call, and notices for turns that ended early.
    /// Session facts, tool results and the service's own notes to the model are
    /// not shown.
    /// </summary>
    public static List<object> Messages(InterviewRecord record)
    {
        var items = new List<object>();
        foreach (var message in record.Messages)
        {
            var node = JsonNode.Parse(message.ContentJson) as JsonObject;
            if (node is null) continue;
            if (message.Role == "notice")
            {
                string code = node["code"]?.GetValue<string>() ?? "";
                items.Add(new { role = "notice", code, text = node["text"]?.GetValue<string>() ?? NoticeText(code) });
                continue;
            }
            var blocks = (node["content"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            if (message.Role == "user")
            {
                // What the user sent: their text and the files they attached, as one bubble.
                var files = blocks.Where(b => b["type"]?.GetValue<string>() == "attachment")
                    .Select(b => new { id = b["id"]?.GetValue<string>(), name = b["name"]?.GetValue<string>(), kind = b["kind"]?.GetValue<string>() }).ToList();
                string typed = string.Join("\n\n", blocks.Where(b => b["type"]?.GetValue<string>() == "text").Select(b => b["text"]?.GetValue<string>() ?? "")
                    .Where(t => !t.StartsWith("[Session facts", StringComparison.Ordinal) && !t.StartsWith("[Note from the interview service]", StringComparison.Ordinal)));
                if (typed.Length > 0 || files.Count > 0) items.Add(new { role = "user", text = typed, attachments = files });
                continue;
            }
            foreach (var block in blocks)
            {
                string type = block["type"]?.GetValue<string>() ?? "";
                string text = block["text"]?.GetValue<string>() ?? "";
                if (message.Role == "assistant" && type == "text" && text.Length > 0)
                    items.Add(new { role = "assistant", text });
                else if (message.Role == "assistant" && type == "tool_use")
                    items.Add(new { role = "tool", text = InterviewTools.Activity(block["name"]?.GetValue<string>() ?? "") });
            }
        }
        return items;
    }

    private static string NoticeText(string code) => code switch
    {
        "refusal" => "The model declined that request. Rephrase it, or ask about a different part of the demo.",
        "round_limit" => "The interviewer used its tool steps without finishing. Send \"continue\" to carry on.",
        "length_limit" => "The answer was cut off twice. Ask for a shorter answer to continue.",
        "unexpected_stop" => "The model stopped unexpectedly. Send a message to continue.",
        "empty_reply" => "The model sent an empty reply. Send a message to continue.",
        _ when code.StartsWith("gage.", StringComparison.Ordinal) => $"The interview stopped because the model could not be reached ({code}). See the banner for the fix.",
        _ => $"The turn ended early ({code})."
    };

    /// <summary>Storyline cards: the chosen ones, then what the user asked for that is not in the simulator yet.</summary>
    private static List<object> Cards(JsonObject needs, InterviewLibrary library)
    {
        var cards = new List<object>();
        foreach (var id in (needs["chosenStorylines"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>())
            if (library.Storylines.Find(id) is { } s)
                cards.Add(new { id = s.Id, title = s.Title, engine = s.Engine, pitch = s.Pitch, evidence = s.Evidence, kind = "chosen", words = (string?)null, offeredInstead = (string?)null });
        foreach (var request in (needs["requests"] as JsonArray ?? []).OfType<JsonObject>())
            cards.Add(new
            {
                id = request["storyline"]?.GetValue<string>(), title = request["title"]?.GetValue<string>() ?? request["words"]?.GetValue<string>(),
                engine = request["engine"]?.GetValue<string>(), pitch = (string?)null, evidence = (int?)null, kind = "requested",
                words = request["words"]?.GetValue<string>(), offeredInstead = request["offeredInstead"]?["title"]?.GetValue<string>()
            });
        return cards;
    }

    /// <summary>
    /// What Approve will create, for the in-page confirmation: name, backfill,
    /// Dataset and tables, computed from the draft with "now" as the anchor.
    /// Null while there is no preview to approve.
    /// </summary>
    private static object? Approval(InterviewRecord record, JsonObject needs, bool ownDatasets, string? sharedDataset, DateTimeOffset nowUtc)
    {
        if (record.DraftModelJson is null || record.PreviewJson is null) return null;
        // An approved interview offers approval again only for a changed plan, which rebuilds the plant.
        string? replaces = record.ApprovedSimulation;
        if (replaces is not null && (needs[InterviewState.ApprovedDraftKey]?.GetValue<string>() is not { } approvedDraft || approvedDraft == InterviewState.DraftHash(record.DraftModelJson))) return null;
        var loaded = PlantModelLoader.Load(record.DraftModelJson, nowUtc);
        if (!loaded.IsValid)
            return new { valid = false, errors = loaded.Errors.Take(5).Select(e => new { e.Code, e.Path, e.Message }) };
        var model = loaded.Model!;
        string? backfill = (JsonNode.Parse(record.DraftModelJson)?["timeline"] as JsonObject)?["backfill"]?.GetValue<string>();
        return new
        {
            valid = true,
            replaces,
            simulation = model.Simulation,
            tagRoot = model.TagRoot,
            tablePrefix = model.TablePrefix,
            backfill,
            historyStartsUtc = model.StartUtc,
            dataset = ownDatasets ? model.Simulation : sharedDataset,
            newDataset = ownDatasets && replaces is null,
            datasetPurgeAgeDays = ownDatasets ? PlantDatasets.PurgeAgeDays : (int?)null,
            tables = PlantSchema.TablesFor(model).Select(t => PlantSchema.PhysicalName(model, t.Purpose)).ToList()
        };
    }
}
