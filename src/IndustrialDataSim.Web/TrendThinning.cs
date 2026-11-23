using System.Text.Json;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Web;

/// <summary>
/// Reduces a tag's raw points to at most 1,000 for a trend chart, keeping what
/// the eye needs: for numbers, the lowest and highest point of each of 500 equal
/// time slices (so a spike survives); for text (states, SKUs, phases), only the
/// points where the value changes.
/// </summary>
public static class TrendThinning
{
    public const int MaxPoints = 1000;
    private const int Buckets = MaxPoints / 2;

    public static (IReadOnlyList<TagPoint> Points, bool Thinned) Thin(IReadOnlyList<TagPoint> raw,
        DateTimeOffset start, DateTimeOffset end, string valueType)
    {
        if (valueType == "number") return raw.Count <= MaxPoints ? (raw, false) : (Numeric(raw, start, end), true);

        // Text and anything else: changes only (plus the first point).
        var changes = new List<TagPoint>();
        foreach (var point in raw)
            if (changes.Count == 0 || !SameValue(changes[^1].Value, point.Value)) changes.Add(point);
        if (changes.Count <= MaxPoints) return (changes, changes.Count < raw.Count);

        // Still too many: the value in force at 1,000 evenly spaced times.
        var sampled = new List<TagPoint>(MaxPoints);
        int index = 0;
        double step = (end - start).Ticks / (double)(MaxPoints - 1);
        for (int i = 0; i < MaxPoints; i++)
        {
            var at = start.AddTicks((long)(step * i));
            while (index + 1 < changes.Count && changes[index + 1].Time <= at) index++;
            if (changes[index].Time > at) continue;
            if (sampled.Count == 0 || !SameValue(sampled[^1].Value, changes[index].Value))
                sampled.Add(changes[index] with { Time = at });
        }
        return (sampled, true);
    }

    private static List<TagPoint> Numeric(IReadOnlyList<TagPoint> raw, DateTimeOffset start, DateTimeOffset end)
    {
        long span = Math.Max(1, (end - start).Ticks);
        var low = new TagPoint?[Buckets];
        var high = new TagPoint?[Buckets];
        foreach (var point in raw)
        {
            if (point.Value.ValueKind != JsonValueKind.Number) continue;
            int bucket = (int)Math.Clamp((point.Time - start).Ticks * Buckets / span, 0, Buckets - 1);
            double value = point.Value.GetDouble();
            if (low[bucket] is not { } l || value < l.Value.GetDouble()) low[bucket] = point;
            if (high[bucket] is not { } h || value > h.Value.GetDouble()) high[bucket] = point;
        }
        var result = new List<TagPoint>(MaxPoints);
        for (int b = 0; b < Buckets; b++)
        {
            if (low[b] is not { } l) continue;
            var h = high[b]!;
            if (ReferenceEquals(l, h)) { result.Add(l); continue; }
            if (l.Time <= h.Time) { result.Add(l); result.Add(h); }
            else { result.Add(h); result.Add(l); }
        }
        return result;
    }

    private static bool SameValue(JsonElement a, JsonElement b) => a.GetRawText() == b.GetRawText();
}
