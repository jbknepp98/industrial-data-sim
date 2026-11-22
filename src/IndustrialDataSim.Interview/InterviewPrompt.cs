using System.Text;
using IndustrialDataSim.Core.Plant;

namespace IndustrialDataSim.Interview;

/// <summary>
/// The interview's system prompt: the shared interview script, a summary of
/// the storyline library and the plant limits.
///
/// The text depends only on the library, so it is identical for every session
/// and every request and caches well. The current date and per-session facts
/// go in the first user turn (<see cref="SessionFacts"/>), never here.
///
/// It is written for whatever model sits behind Gage, which may be older than
/// the newest Claude: plain numbered instructions, no reliance on prefill,
/// and tools with strict schemas.
/// </summary>
public static class InterviewPrompt
{
    /// <summary>Plant limits the model must respect. They mirror PlantModelLoader; the loader stays the authority.</summary>
    internal const string PlantLimits = """
        - At most 2 production lines, 6 SKUs, 8 operators and 4 technicians.
        - The simulation name is PascalCase: 2 to 32 letters or digits, starting with an upper-case letter (for example AcmeNuggs). Its lower-case form prefixes every table, and it names the tag root.
        - Backfill (days of history before now) is a whole number followed by d or h, for example "6d". It must fit the Historian Dataset's purge age; 6 days is a safe default for a 7-day purge.
        - The preview covers the backfill plus 1 to 72 hours after now (24 by default).
        """;

    public static string Build(InterviewLibrary library)
    {
        var text = new StringBuilder();
        text.AppendLine("""
            You are the Flow Data Simulator interviewer inside the Timebase stack. You talk with a Timebase user who is preparing a demo or proof of concept. Your job is to learn who will watch and what costs them the most, choose storylines from the library that make that audience see their own problem, compose a small simulated plant that shows those storylines, and present a preview for the user to approve.

            How you work:
            1. Follow the interview script below. Ask a few questions at a time, in plain language, and use the audience's own words.
            2. Call record_audience as soon as you know the industry, the roles in the room and their main problems, and again whenever the profile changes. Map the user's words to the library vocabulary yourself (industries, roles and KPIs are listed below).
            3. Call find_storylines to rank the library for the recorded audience. The ranking is computed by code; present its top matches in the audience's words and quote each storyline's evidence (the number of customer meetings that raised it). Never invent storylines that are not in the library.
            4. Every find_storylines call lists, in askedFor, each story the user explicitly asked for so far, mapped to its library id (or "none" when nothing in the library covers it). The service saves the asks whose engine status is planned or later, or that map to none, for the demo guide. Say plainly that those are not in the simulator yet, and offer the storyline find_storylines names in offerInstead. When the user asks for something new later, call find_storylines again with it in askedFor.
            5. Call compose_model with the chosen available storylines and the user's names. If it returns errors, fix every reported field and call it again. Do not ask the user about errors you can fix yourself.
            6. Call preview_plant once the model is valid. Explain the headline numbers and, for each storyline, where and when it appears (times, tickets, holds, uncertified assignments). Times in tool results are UTC; say so, or convert them using the time given in the session facts.
            7. Ask the user to approve the preview or say what to change. Repeat compose_model and preview_plant until they are satisfied.

            Things you never do:
            - You cannot build or run a plant, and you have no tool for it. Building and running start only when the user clicks "Approve and run" under the preview in this page. When the preview is ready, tell the user to review it and click that button. Never say a plant is built, running or writing data.
            - Never promise a storyline whose engine status is not "available".
            - Never ask for passwords, keys or client secrets. The simulator already reaches Pulse with its own client.
            - Keep plants small, as the limits below require.

            """);
        text.AppendLine("# Interview script").AppendLine();
        text.AppendLine(library.Script.Trim()).AppendLine();
        text.AppendLine("# Storyline library").AppendLine();
        text.AppendLine("Engine status: available = buildable now; planned = on the roadmap; later = not scheduled. \"closest\" names the available storyline to offer instead.").AppendLine();
        foreach (var storyline in library.Storylines.Storylines) AppendStoryline(text, storyline);
        text.AppendLine();
        text.AppendLine("# Vocabulary for record_audience").AppendLine();
        text.AppendLine("Industries: " + string.Join(", ", PlantStorylines.Industries));
        text.AppendLine("Roles: " + string.Join(", ", PlantStorylines.Roles));
        text.AppendLine("KPIs used by the library: " + string.Join(", ", library.Storylines.Storylines.SelectMany(s => s.Kpis).Distinct(StringComparer.OrdinalIgnoreCase)));
        text.AppendLine();
        text.AppendLine("# Plant limits").AppendLine();
        text.AppendLine(PlantLimits.TrimEnd());
        return text.ToString();
    }

    private static void AppendStoryline(StringBuilder text, PlantStoryline storyline)
    {
        text.Append("- ").Append(storyline.Id).Append(" — ").Append(storyline.Title)
            .Append(" [").Append(storyline.Engine).Append(']');
        if (storyline.Closest is { } closest) text.Append(" closest: ").Append(closest);
        if (!storyline.Available && storyline.Closest is null) text.Append(" closest: none");
        text.AppendLine();
        text.Append("  Pitch: ").AppendLine(storyline.Pitch);
        text.Append("  Industries: ").Append(string.Join(", ", storyline.Industries))
            .Append(". Roles: ").Append(string.Join(", ", storyline.Roles))
            .Append(". KPIs: ").Append(string.Join(", ", storyline.Kpis))
            .Append(". Evidence: ").Append(storyline.Evidence).Append(" meetings");
        if (storyline.Requires.Count > 0) text.Append(". Requires: ").Append(string.Join(", ", storyline.Requires));
        if (storyline.Base is { } baseId) text.Append(". Base: ").Append(baseId);
        text.AppendLine(".");
    }

    /// <summary>
    /// The per-session facts sent at the start of the first user turn. Kept out
    /// of the system prompt so the system prompt never changes between sessions.
    /// </summary>
    public static string SessionFacts(Guid interviewId, DateTimeOffset nowUtc) =>
        $"[Session facts, not typed by the user] Interview {interviewId:D}. Current time: {nowUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC ({nowUtc.UtcDateTime:dddd}). Each preview uses the time it runs as \"now\" and reports it as anchorUtc.";
}
