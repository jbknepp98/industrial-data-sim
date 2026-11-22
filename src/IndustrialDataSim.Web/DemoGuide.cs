using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IndustrialDataSim.Core.Plant;
using IndustrialDataSim.Interview;

namespace IndustrialDataSim.Web;

/// <summary>
/// The one-page demo guide for an approved plant, filled from the template
/// examples/storylines/demo-guide.md:
///
/// - the needs profile (audience, problems, systems) and the unmet asks
///   recorded during the interview;
/// - each chosen storyline's pitch and talk track, with the plant's real tag
///   root and table prefix;
/// - each storyline's "moment" with real times. The admitted model is run
///   again offline at its stored anchor, so the times are exactly those the
///   live plant writes (the interview preview ran at an earlier "now").
///
/// The template marks the storyline block with &lt;!-- storyline … --&gt; and
/// &lt;!-- end storyline --&gt;, and the talk-track step line with
/// &lt;!-- step … --&gt;. Other HTML comments are notes for authors and are
/// dropped from the output.
/// </summary>
public static partial class DemoGuide
{
    public sealed record Result(string Markdown, string Html);

    private const string StorylineStart = "<!-- storyline", StorylineEnd = "<!-- end storyline -->", StepStart = "<!-- step";

    /// <param name="needsJson">The interview's needs profile (flowsim_interviews.needs_json).</param>
    /// <param name="model">The admitted model, loaded at its stored anchor.</param>
    /// <param name="nowUtc">Moments after this time are left out: they have not happened in the live plant yet.</param>
    public static Result Build(string template, InterviewLibrary library, string? needsJson, PlantModel model, string? dataset, DateTimeOffset nowUtc)
    {
        var needs = needsJson is null ? new JsonObject() : JsonNode.Parse(needsJson) as JsonObject ?? new JsonObject();
        var audience = needs["audience"] as JsonObject ?? new JsonObject();
        var chosen = Strings(needs["chosenStorylines"]).Select(library.Storylines.Find).OfType<PlantStoryline>().ToList();

        // The live plant's history up to now (at most a week past its anchor, to bound the cost).
        var end = Max(model.AnchorUtc.AddMinutes(1), Min(nowUtc, model.AnchorUtc.AddDays(7)));
        if (model.EndUtc is { } modelEnd && modelEnd < end) end = modelEnd;
        var run = PlantEngine.Run(model, end);
        var tables = PlantPreview.FinalTables(run);
        var document = JsonSerializer.SerializeToNode(PlantPreview.Build(run, 3600), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!.AsObject();
        var summary = document["summary"] as JsonObject ?? new JsonObject();

        string problems = Join(Strings(audience["pains"]), "; ", "not recorded");
        var values = new Dictionary<string, string>
        {
            ["PlantName"] = model.Simulation,
            ["Roles"] = Join(Strings(audience["roles"]).Select(Readable), ", ", "audience not recorded"),
            ["Industry"] = audience["industry"]?.GetValue<string>() is { Length: > 0 } industry ? Readable(industry) : "industry not recorded",
            ["Problems"] = problems,
            ["Systems"] = Join(Strings(audience["systems"]), ", ", "Not recorded"),
            ["Simulation"] = model.Simulation,
            ["TagRoot"] = model.TagRoot,
            ["prefix"] = model.TablePrefix,
            ["StartUtc"] = model.StartUtc.UtcDateTime.ToString("ddd MMM d HH:mm 'UTC'", CultureInfo.InvariantCulture),
            ["Dataset"] = string.IsNullOrWhiteSpace(dataset) ? "not recorded" : dataset,
            ["UnmetRequests"] = UnmetRequests(needs),
            ["Headline"] = Headline(summary)
        };

        string text = template.Replace("\r\n", "\n");
        int blockStart = text.IndexOf(StorylineStart, StringComparison.Ordinal);
        int blockEnd = text.IndexOf(StorylineEnd, StringComparison.Ordinal);
        if (blockStart < 0 || blockEnd < blockStart)
            throw new InvalidOperationException("The demo guide template has no <!-- storyline … --> … <!-- end storyline --> block.");
        string block = text[(text.IndexOf('\n', blockStart) + 1)..blockEnd];
        int stepAt = block.IndexOf(StepStart, StringComparison.Ordinal);
        if (stepAt < 0) throw new InvalidOperationException("The demo guide template's storyline block has no <!-- step … --> line.");
        string storylineTemplate = block[..stepAt];
        string stepTemplate = block[(block.IndexOf('\n', stepAt) + 1)..];

        var storylines = new StringBuilder();
        string? baseId = PlantStorylines.CommonBase(chosen); // the base the interview composed on
        for (int i = 0; i < chosen.Count; i++)
        {
            var storyline = chosen[i];
            var talkTrack = storyline.TalkTrackFor(baseId);
            var moments = InterviewTools.Appearances(storyline, model, run, tables, document, baseId);
            var storylineValues = new Dictionary<string, string>(values)
            {
                ["StorylineNumber"] = (i + 1).ToString(CultureInfo.InvariantCulture),
                ["StorylineTitle"] = storyline.Title,
                ["Pitch"] = storyline.Pitch,
                ["StorylineRoles"] = Join(storyline.Roles.Select(Readable), ", ", "everyone"),
                ["StorylineKpis"] = Join(storyline.Kpis, ", ", "none listed"),
                ["Moment"] = Moment(moments["moments"] as JsonArray ?? [], nowUtc)
            };
            storylines.Append(Fill(storylineTemplate, storylineValues));
            for (int s = 0; s < talkTrack.Count; s++)
            {
                var step = talkTrack[s];
                var targets = step.Tags.Concat(step.Tables).Select(t => "`" + Fill(t, values) + "`").ToList();
                storylines.Append(Fill(stepTemplate, new Dictionary<string, string>(storylineValues)
                {
                    ["StepNumber"] = (s + 1).ToString(CultureInfo.InvariantCulture),
                    ["Tool"] = step.Tool,
                    ["TagsOrTables"] = targets.Count > 0 ? string.Join(", ", targets) : step.Tool == "Gage" ? "a Gage chat" : step.Tool,
                    ["Ask"] = Sentence(Fill(step.Ask, values)),
                    ["Expect"] = Sentence(Fill(step.Expect, values))
                }));
            }
            storylines.Append('\n');
        }
        if (chosen.Count == 0) storylines.Append("No storylines were recorded for this plant.\n\n");

        string filled = Fill(text[..blockStart], values) + storylines + Fill(text[(blockEnd + StorylineEnd.Length)..], values);
        string markdown = BlankLines().Replace(Comments().Replace(filled, ""), "\n\n").Trim() + "\n";
        return new(markdown, ToHtml(markdown, model.Simulation));
    }

    // ---------------- sections ----------------

    private static string UnmetRequests(JsonObject needs)
    {
        var requests = (needs["requests"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (requests.Count == 0) return "Nothing: everything they asked for is in this plant.";
        return string.Join("\n", requests.Select(r =>
        {
            string words = r["words"]?.GetValue<string>() ?? "";
            string? title = r["title"]?.GetValue<string>();
            string engine = r["engine"]?.GetValue<string>() switch
            {
                "planned" => "planned for the simulator",
                "later" => "not scheduled yet",
                "unmapped" or null when title is null => "no storyline covers it yet",
                var other => other!
            };
            string instead = r["offeredInstead"]?["title"]?.GetValue<string>() is { } offer ? $"; offered \"{offer}\" instead" : "";
            return $"- {words}" + (title is null ? "" : $" (\"{title}\")") + $": {engine}{instead}.";
        }));
    }

    private static string Headline(JsonObject summary)
    {
        var parts = new List<string>();
        if (summary["onTimePercent"]?.GetValue<double>() is { } onTime)
            parts.Add($"{onTime.ToString(CultureInfo.InvariantCulture)}% of {summary["completedOrders"]} completed orders on time");
        if (summary["breakdowns"] is { } breakdowns) parts.Add($"{breakdowns} breakdowns");
        if (summary["meanMinutesToRepair"]?.GetValue<double>() is { } repair) parts.Add($"mean repair {Math.Round(repair)} min");
        if (summary["qualityHolds"]?.GetValue<int>() is > 0 and var holds) parts.Add($"{holds} lots held by Quality");
        return parts.Count == 0 ? "the plant's history so far" : string.Join(", ", parts) + " so far";
    }

    /// <summary>One sentence per kind of moment, with real times. Only what has already happened in the live plant.</summary>
    private static string Moment(JsonArray moments, DateTimeOffset nowUtc)
    {
        var sentences = new List<string>();
        var breakdowns = moments.OfType<JsonObject>().Where(m => Kind(m) == "breakdown" && Utc(m["failedUtc"]) is { } failed && failed <= nowUtc).ToList();
        // The planted breakdown the walk-through uses is the most recent one before now.
        if (breakdowns.LastOrDefault() is { } b)
        {
            var ticket = (b["tickets"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault();
            var text = new StringBuilder($"{b["equipment"]} on {b["line"]} ({b["failureMode"]})");
            if (Utc(b["warningStartsUtc"]) is { } warning) text.Append($": vibration starts rising {Time(warning)}, the line stops {Time(Utc(b["failedUtc"])!.Value)}");
            else text.Append($" fails {Time(Utc(b["failedUtc"])!.Value)}");
            if (ticket is not null && Utc(ticket["createdUtc"]) is { } created) text.Append($", and ticket {ticket["ticket"]} opens {Time(created)}");
            if (ticket is not null && Utc(ticket["completedUtc"]) is { } completed && completed <= nowUtc) text.Append($"; it is closed {Time(completed)}");
            text.Append('.');
            if (breakdowns.Count > 1) text.Append($" The same failure also happened {string.Join(" and ", breakdowns.SkipLast(1).Select(e => Time(Utc(e["failedUtc"])!.Value)))}.");
            sentences.Add(text.ToString());
        }
        foreach (var m in moments.OfType<JsonObject>())
        {
            switch (Kind(m))
            {
                case "quality excursion" when Utc(m["fromUtc"]) is { } from && from <= nowUtc:
                {
                    var first = (m["firstHolds"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault();
                    sentences.Add($"{m["parameter"]} runs out of specification from {Time(from)} to {Time(Utc(m["untilUtc"])!.Value)}; {m["holds"]} lots are held" +
                        (first is not null && Utc(first["createdUtc"]) is { } held ? $", the first ({first["hold"]}, lot {first["lot"]}) {Time(held)}." : "."));
                    break;
                }
                case "uncertified assignments" when m["count"]?.GetValue<int>() > 0:
                {
                    var first = (m["first"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault();
                    sentences.Add($"{m["count"]} safety-critical tickets go to a technician without a current certificate" +
                        (first is not null && Utc(first["assignedUtc"]) is { } at ? $"; the first is {first["ticket"]} ({first["technician"]}) {Time(at)}." : "."));
                    break;
                }
                case "uncertified runs" when m["count"]?.GetValue<int>() > 0:
                {
                    var first = (m["first"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault();
                    sentences.Add($"{m["count"]} runs start with an operator without a current work-instruction certificate" +
                        (first is not null && Utc(first["startUtc"]) is { } at ? $"; the first is {first["run"]} on {first["line"]} {Time(at)}." : "."));
                    break;
                }
                case "demand" when m["onTimePercent"] is { } onTime:
                    sentences.Add($"{onTime}% of {m["completedOrders"]} completed orders shipped on time.");
                    break;
            }
        }
        return sentences.Count == 0 ? "It shows across the whole history rather than at one planted moment." : string.Join(" ", sentences);
    }

    // ---------------- helpers ----------------

    private static string Fill(string text, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);

    /// <summary>Ends the text with a full stop unless it already ends with punctuation (the template puts none after it).</summary>
    private static string Sentence(string text) => text.Length == 0 || ".?!".Contains(text[^1]) ? text : text + ".";

    private static string? Kind(JsonObject moment) => moment["kind"]?.GetValue<string>();

    private static DateTimeOffset? Utc(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue(out DateTimeOffset time)) return time.ToUniversalTime();
        return value.TryGetValue(out string? text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.ToUniversalTime() : null;
    }

    /// <summary>"on Thu Nov 20 at 14:10 UTC": the times Explorer and Connect show, in UTC.</summary>
    private static string Time(DateTimeOffset utc) => "on " + utc.UtcDateTime.ToString("ddd MMM d 'at' HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static IEnumerable<string> Strings(JsonNode? node) =>
        (node as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>().Where(s => s.Length > 0);

    private static string Join(IEnumerable<string> items, string separator, string empty)
    {
        var list = items.ToList();
        return list.Count == 0 ? empty : string.Join(separator, list);
    }

    /// <summary>Library vocabulary in plain words: "plant-manager" → "plant manager", "ci-oee" → "CI / OEE".</summary>
    private static string Readable(string id) => id switch
    {
        "ci-oee" => "CI / OEE",
        "it-ot" => "IT / OT",
        "food-beverage" => "food and beverage",
        "cpg" => "consumer goods",
        _ => id.Replace('-', ' ')
    };

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    [GeneratedRegex(@"\{([A-Za-z]+)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"<!--.*?-->\n?", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    // ---------------- Markdown → HTML (the subset the template uses) ----------------

    /// <summary>
    /// Headings, paragraphs, numbered and bulleted lists, pipe tables, and
    /// inline **bold**, *italic* and `code`. All text is HTML-encoded first.
    /// </summary>
    internal static string ToHtml(string markdown, string title)
    {
        var html = new StringBuilder();
        html.Append("<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append("<title>Demo guide: ").Append(WebUtility.HtmlEncode(title)).Append("</title><style>")
            .Append(":root{color-scheme:light dark;--bg:#F2F2F2;--fg:#1F1F23;--muted:#374151;--rule:#E5E7EB;--accent:#1B81AD}")
            .Append("@media (prefers-color-scheme:dark){:root{--bg:#1F1F23;--fg:#F2F2F2;--muted:#D1D5DB;--rule:#3F3F46;--accent:#31AEE4}}")
            .Append("body{background:var(--bg);color:var(--fg);font:16px/1.55 Inter,system-ui,sans-serif;max-width:820px;margin:0 auto;padding:32px 16px 64px}")
            .Append("h1,h2,h3{font-family:Poppins,Inter,system-ui,sans-serif;line-height:1.25}h2{border-top:1px solid var(--rule);padding-top:20px;margin-top:32px}")
            .Append("code{font-family:'JetBrains Mono',ui-monospace,Menlo,monospace;font-size:.88em}table{border-collapse:collapse}td,th{border-bottom:1px solid var(--rule);padding:6px 12px 6px 0;text-align:left;vertical-align:top}")
            .Append("a{color:var(--accent)}li{margin:4px 0}</style></head><body>\n");
        var lines = markdown.Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i];
            if (line.Trim().Length == 0) { i++; continue; }
            if (line.StartsWith('#'))
            {
                int level = line.TakeWhile(c => c == '#').Count();
                html.Append($"<h{level}>{Inline(line[level..].Trim())}</h{level}>\n");
                i++;
            }
            else if (line.StartsWith('|'))
            {
                html.Append("<table>\n");
                for (; i < lines.Length && lines[i].StartsWith('|'); i++)
                {
                    var cells = lines[i].Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();
                    if (cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':'))) continue;
                    if (cells.All(c => c.Length == 0)) continue;
                    html.Append("<tr>").Append(string.Concat(cells.Select(c => $"<td>{Inline(c)}</td>"))).Append("</tr>\n");
                }
                html.Append("</table>\n");
            }
            else if (NumberedItem().IsMatch(line) || line.StartsWith("- "))
            {
                bool numbered = !line.StartsWith("- ");
                html.Append(numbered ? "<ol>\n" : "<ul>\n");
                while (i < lines.Length && (numbered ? NumberedItem().IsMatch(lines[i]) : lines[i].StartsWith("- ")))
                {
                    var item = new StringBuilder(numbered ? NumberedItem().Replace(lines[i], "", 1) : lines[i][2..]);
                    // Continuation lines belong to the item until a blank line or the next item.
                    for (i++; i < lines.Length && lines[i].Trim().Length > 0 && !NumberedItem().IsMatch(lines[i]) && !lines[i].StartsWith("- ") && !lines[i].StartsWith('#'); i++)
                        item.Append(' ').Append(lines[i].Trim());
                    html.Append("<li>").Append(Inline(item.ToString())).Append("</li>\n");
                    while (i < lines.Length && lines[i].Trim().Length == 0 && i + 1 < lines.Length && (numbered ? NumberedItem().IsMatch(lines[i + 1]) : lines[i + 1].StartsWith("- "))) i++;
                }
                html.Append(numbered ? "</ol>\n" : "</ul>\n");
            }
            else
            {
                var paragraph = new StringBuilder();
                for (; i < lines.Length && lines[i].Trim().Length > 0 && !lines[i].StartsWith('#') && !lines[i].StartsWith('|'); i++)
                    paragraph.Append(paragraph.Length > 0 ? " " : "").Append(lines[i].Trim());
                html.Append("<p>").Append(Inline(paragraph.ToString())).Append("</p>\n");
            }
        }
        return html.Append("</body></html>\n").ToString();
    }

    private static string Inline(string text)
    {
        string encoded = WebUtility.HtmlEncode(text);
        encoded = CodeSpan().Replace(encoded, "<code>$1</code>");
        encoded = BoldSpan().Replace(encoded, "<strong>$1</strong>");
        return ItalicSpan().Replace(encoded, "<em>$1</em>");
    }

    [GeneratedRegex(@"^\d+\.\s")]
    private static partial Regex NumberedItem();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex BoldSpan();

    [GeneratedRegex(@"(?<![*\w])\*([^*]+)\*(?![*\w])")]
    private static partial Regex ItalicSpan();
}
