using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Core.Configuration;

/// <summary>
/// Turns a schemaVersion 2 session's relative <c>backfill</c> (for example
/// "72h") into a concrete <c>startUtc</c> once, at validation, preview or
/// admission time. The admitted model is therefore fully concrete: restarts
/// never re-resolve the start, so clocks, seeds and cursors stay identical.
/// </summary>
public static partial class BackfillResolver
{
    private const long MaximumDays = 3650;

    /// <summary>
    /// Returns the model with <c>backfill</c> replaced by <c>startUtc</c>, aligned
    /// down to the sampling grid (or to whole seconds for a bare session header).
    /// Input without <c>backfill</c> is returned unchanged, byte for byte.
    /// </summary>
    public static string Resolve(string json, DateTimeOffset nowUtc, out ValidationError? error)
    {
        error = null;
        // JsonNode reads lazily, so duplicate properties surface on first access.
        // Any such problem is left for the strict loader to report precisely.
        try { return ResolveCore(json, nowUtc, ref error); }
        catch (Exception problem) when (problem is JsonException or ArgumentException or InvalidOperationException)
        {
            error = null;
            return json;
        }
    }

    private static string ResolveCore(string json, DateTimeOffset nowUtc, ref ValidationError? error)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { return json; }
        if (root is not JsonObject model) return json;
        // A full model nests the header under "session"; a header file is the header.
        bool nested = model["session"] is JsonObject;
        var header = nested ? (JsonObject)model["session"]! : model;
        string path = nested ? "$.session" : "$";
        if (!header.TryGetPropertyValue("backfill", out var backfill)) return json;
        if (header.ContainsKey("startUtc"))
        {
            error = new("session.start_conflict", path + ".startUtc", "Use either startUtc or backfill, not both.");
            return json;
        }
        if (header["schemaVersion"] is not JsonValue v || !v.TryGetValue(out int schema) || schema != 2)
            return json; // The loader explains that backfill needs schemaVersion 2.
        if (backfill is not JsonValue text || !text.TryGetValue(out string? value) || Parse(value) is not { } span)
        {
            error = new("session.invalid_backfill", path + ".backfill",
                "Use a whole number followed by s, m, h or d (for example \"72h\" or \"7d\"), from 1 second up to 3650 days.");
            return json;
        }
        long interval = TimeSpan.TicksPerSecond;
        if (nested && model["samplingIntervalMs"] is JsonValue sampling && sampling.TryGetValue(out long ms) && ms > 0 &&
            ms <= long.MaxValue / TimeSpan.TicksPerMillisecond)
            interval = ms * TimeSpan.TicksPerMillisecond;
        long startTicks = nowUtc.UtcTicks - span.Ticks;
        startTicks -= startTicks % interval; // Align to the grid so timestamps are tidy.
        var start = new DateTimeOffset(startTicks, TimeSpan.Zero);
        // Rebuild the header with startUtc where backfill was, keeping field order.
        var rebuilt = new JsonObject();
        foreach (var (name, node) in header.ToList())
        {
            header.Remove(name);
            if (name == "backfill") rebuilt["startUtc"] = Format(start);
            else rebuilt[name] = node;
        }
        if (nested) model["session"] = rebuilt;
        else return rebuilt.ToJsonString();
        return model.ToJsonString();
    }

    private static TimeSpan? Parse(string? value)
    {
        if (value is null) return null;
        var match = Pattern().Match(value);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long amount))
            return null;
        long seconds = match.Groups[2].Value switch
        {
            "s" => amount,
            "m" => amount * 60,
            "h" => amount * 3600,
            _ => amount * 86400
        };
        return seconds is >= 1 and <= MaximumDays * 86400 ? TimeSpan.FromSeconds(seconds) : null;
    }

    private static string Format(DateTimeOffset utc)
    {
        string text = utc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        long fraction = utc.UtcTicks % TimeSpan.TicksPerSecond;
        return fraction == 0 ? text + "Z" : text + "." + fraction.ToString("D7", CultureInfo.InvariantCulture) + "Z";
    }

    [GeneratedRegex("^([1-9][0-9]{0,7})(s|m|h|d)$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
