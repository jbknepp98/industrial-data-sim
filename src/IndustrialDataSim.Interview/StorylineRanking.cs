using System.Text.RegularExpressions;
using IndustrialDataSim.Core.Plant;

namespace IndustrialDataSim.Interview;

/// <summary>The audience facts the ranking uses, from record_audience.</summary>
public sealed record AudienceProfile(string Industry, IReadOnlyList<string> Roles, IReadOnlyList<string> Kpis);

/// <summary>What to offer instead of a storyline the engine cannot build yet, and why.</summary>
public sealed record StorylineOffer(string Id, string Title, string Reason);

public sealed record RankedStoryline(PlantStoryline Storyline, int Score, IReadOnlyList<string> Reasons, StorylineOffer? OfferInstead);

/// <summary>
/// Deterministic storyline ranking for find_storylines. The order is computed
/// here, never guessed by the model, so the same audience always gets the
/// same storylines in the same order.
///
/// Points (the interview script's order: industry first, then roles, then KPIs):
/// - industry match: 4;
/// - each role in the room the storyline speaks to: 2;
/// - each storyline KPI that shares a significant word with a recorded KPI: 2;
/// - each keyword the model passes (the user's own words for what they want
///   to see, for example "energy spikes"): 2 when all its words appear in the
///   storyline's id, title, pitch or KPIs, 1 when some do.
///
/// Ties go to available storylines, then more evidence, then the id, so the
/// order never depends on dictionary or library order.
/// </summary>
public static partial class StorylineRanking
{
    public const int IndustryPoints = 4, RolePoints = 2, KpiPoints = 2, KeywordPoints = 2;

    /// <summary>Words too common to count as a match. "time" is here so "on-time delivery" and "cycle time" do not match each other.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "or", "the", "of", "to", "for", "in", "on", "at", "by", "with", "per", "our", "their", "they", "we",
        "is", "are", "be", "it", "its", "that", "this", "from", "as", "into", "rate", "time", "show", "see", "demo"
    };

    public static IReadOnlyList<RankedStoryline> Rank(PlantStorylineLibrary library, AudienceProfile profile, IReadOnlyList<string> keywords)
    {
        var profileKpiWords = profile.Kpis.SelectMany(Words).ToHashSet(StringComparer.Ordinal);
        var keywordWords = keywords.Select(k => Words(k).ToList()).Where(w => w.Count > 0).ToList();

        var scored = new List<(PlantStoryline Storyline, int Score, List<string> Reasons)>();
        foreach (var storyline in library.Storylines)
        {
            int score = 0;
            var reasons = new List<string>();
            if (storyline.Industries.Contains(profile.Industry))
            {
                score += IndustryPoints;
                reasons.Add($"fits the {profile.Industry} industry");
            }
            var roles = storyline.Roles.Where(profile.Roles.Contains).ToList();
            if (roles.Count > 0)
            {
                score += RolePoints * roles.Count;
                reasons.Add("speaks to " + string.Join(", ", roles));
            }
            var kpis = storyline.Kpis.Where(k => Words(k).Any(profileKpiWords.Contains)).ToList();
            if (kpis.Count > 0)
            {
                score += KpiPoints * kpis.Count;
                reasons.Add("moves their KPIs: " + string.Join(", ", kpis));
            }
            var storyWords = StoryWords(storyline);
            var matchedKeywords = new List<string>();
            foreach (var words in keywordWords)
            {
                int hits = words.Count(storyWords.Contains);
                if (hits == 0) continue;
                score += hits == words.Count ? KeywordPoints : KeywordPoints / 2;
                matchedKeywords.Add(string.Join(" ", words));
            }
            if (matchedKeywords.Count > 0) reasons.Add("matches what they asked for: " + string.Join(", ", matchedKeywords));
            if (score > 0) scored.Add((storyline, score, reasons));
        }

        var ordered = scored
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.Storyline.Available)
            .ThenByDescending(s => s.Storyline.Evidence)
            .ThenBy(s => s.Storyline.Id, StringComparer.Ordinal)
            .ToList();
        // Best available match for this audience: the fallback offer for a
        // storyline whose library entry names no closest one.
        var bestAvailable = ordered.Select(s => s.Storyline).FirstOrDefault(s => s.Available)
            ?? library.Storylines.Where(s => s.Available).OrderByDescending(s => s.Evidence).ThenBy(s => s.Id, StringComparer.Ordinal).FirstOrDefault();

        return ordered.Select(s => new RankedStoryline(s.Storyline, s.Score, s.Reasons, Offer(library, s.Storyline, bestAvailable))).ToList();
    }

    private static StorylineOffer? Offer(PlantStorylineLibrary library, PlantStoryline storyline, PlantStoryline? bestAvailable)
    {
        if (storyline.Available) return null;
        if (storyline.Closest is { } closestId && library.Find(closestId) is { } closest)
            return new(closest.Id, closest.Title, "The library names it as the closest available storyline.");
        if (bestAvailable is null) return null;
        return new(bestAvailable.Id, bestAvailable.Title,
            "Nothing in the library is close to this story yet; this is the best available match for the recorded audience. Say so plainly.");
    }

    private static HashSet<string> StoryWords(PlantStoryline storyline) =>
        Words(storyline.Id + " " + storyline.Title + " " + storyline.Pitch + " " + string.Join(" ", storyline.Kpis)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Lower-case significant words, with a plural "s" removed so "spikes" matches "spike".</summary>
    internal static IEnumerable<string> Words(string text)
    {
        foreach (Match match in WordPattern().Matches(text.ToLowerInvariant()))
        {
            string word = match.Value;
            if (StopWords.Contains(word)) continue;
            if (word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal)) word = word[..^1];
            yield return word;
        }
    }

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex WordPattern();
}
