using System.Text.Json;
using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Core.Configuration;

/// <summary>Build an exact-duration hold schedule before generating any samples.</summary>
internal static class RandomIntegerHoldDefinitionLoader
{
    private const long MaximumDurationMs = long.MaxValue / TimeSpan.TicksPerMillisecond;
    internal const int MaximumHolds = 10_000;

    internal static GeneratorDefinition? Load(JsonElement generator, OutputTagDefinition? output,
        string path, ValidationErrors errors, ref int remainingHolds)
    {
        long initialErrors = errors.ErrorCount;
        if (output is not null && output.ValueType != "number")
            errors.Add(new("simulation.random_integer_requires_number", path + ".tag", "Declare this output as number."));
        long minimum = ReadInteger(generator, "minimum", int.MinValue, int.MaxValue, path, errors);
        long maximum = ReadInteger(generator, "maximum", int.MinValue, int.MaxValue, path, errors);
        long seed = ReadInteger(generator, "seed", 0, uint.MaxValue, path, errors);
        long duration = ReadInteger(generator, "durationMs", 1, MaximumDurationMs, path, errors);
        string? adjacent = SessionDefinitionLoader.ReadString(generator, "adjacentValues", path, errors);
        string? after = SessionDefinitionLoader.ReadString(generator, "afterDuration", path, errors);
        if (adjacent is not null && adjacent is not ("allowRepeat" or "requireChange"))
            errors.Add(new("simulation.invalid_adjacent_values", path + ".adjacentValues", "Use allowRepeat or requireChange."));
        if (after is not null && after != "holdLast")
            errors.Add(new("simulation.invalid_after_duration", path + ".afterDuration", "Use holdLast."));
        if (minimum > maximum || (minimum == maximum && adjacent == "requireChange"))
            errors.Add(new("simulation.invalid_integer_range", path + ".maximum",
                "Use an ordered integer range with at least two values for requireChange."));

        bool fixedHold = generator.TryGetProperty("holdDurationMs", out _);
        bool rangedHold = generator.TryGetProperty("holdDurationRangeMs", out var range);
        long lower = 0;
        long upper = 0;
        if (fixedHold == rangedHold)
            errors.Add(new("simulation.invalid_hold_duration", path, "Supply exactly one hold duration or hold duration range."));
        else if (fixedHold)
            lower = upper = ReadInteger(generator, "holdDurationMs", 1, MaximumDurationMs, path, errors);
        else if (range.ValueKind != JsonValueKind.Object)
            errors.Add(new("simulation.invalid_hold_duration", path + ".holdDurationRangeMs", "Expected minimum and maximum durations."));
        else
        {
            string rangePath = path + ".holdDurationRangeMs";
            SessionDefinitionLoader.CheckProperties(range, ["minimum", "maximum"], rangePath, errors);
            lower = ReadInteger(range, "minimum", 1, MaximumDurationMs, rangePath, errors);
            upper = ReadInteger(range, "maximum", 1, MaximumDurationMs, rangePath, errors);
            if (lower > upper)
                errors.Add(new("simulation.invalid_hold_duration", rangePath, "Maximum must be at least minimum."));
        }
        if (errors.ErrorCount != initialErrors) return null;

        // Feasibility: N * lower <= duration <= N * upper. Ceiling division
        // avoids overflow; a resource cap bounds allocation before any planning.
        long fewest = (duration - 1) / upper + 1;
        long most = Math.Min(duration / lower, MaximumHolds);
        if (fewest > most)
        {
            errors.Add(new("simulation.infeasible_hold_schedule", path + ".durationMs",
                "No exact schedule fits the hold bounds within the 10000-hold pattern limit."));
            return null;
        }
        int count = (int)StableDurationChoice.Choose("integer-hold-count-v1", (uint)seed, 0, fewest, most);
        if (count > remainingHolds)
        {
            // Do not alter a tag's random choices to squeeze it into the global
            // limit: that would make its behavior depend on generator ordering.
            errors.Add(new("simulation.hold_schedule_limit", path,
                "Resolved random-integer schedules exceed 10000 total holds per model."));
            return null;
        }
        remainingHolds -= count;
        var steps = new List<StaircaseStep>(count);
        long remaining = duration;
        long previous = 0;
        for (int i = 0; i < count; i++)
        {
            long later = count - i - 1;
            // Both remaining minima and remaining capacity matter. This prevents
            // a short final remainder or an oversized final hold. Products fit
            // Int64: at most 9999 * MaximumDurationMs < long.MaxValue.
            long low = Math.Max(lower, remaining - later * upper);
            long high = Math.Min(upper, remaining - later * lower);
            long dwell = StableDurationChoice.Choose("integer-hold-duration-v1", (uint)seed, i, low, high);
            long value;
            if (i > 0 && adjacent == "requireChange")
            {
                // Draw from a compressed range then skip the previous integer.
                // No retry loop, including a two-value range or Int32 extremes.
                value = StableDurationChoice.Choose("integer-hold-value-v1", (uint)seed, i, minimum, maximum - 1);
                if (value >= previous) value++;
            }
            else value = StableDurationChoice.Choose("integer-hold-value-v1", (uint)seed, i, minimum, maximum);
            remaining -= dwell;
            steps.Add(new((duration - remaining) * TimeSpan.TicksPerMillisecond, JsonSerializer.SerializeToElement(value)));
            previous = value;
        }
        // Reuse half-open interval lookup and explicit holdLast behavior. This
        // returns a finite schedule that a sequence can compose; delivery state
        // remains outside this generator.
        return new StaircaseGeneratorDefinition(steps.AsReadOnly());
    }

    private static long ReadInteger(JsonElement obj, string name, long minimum, long maximum,
        string path, ValidationErrors errors)
    {
        if (obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out long number) && number >= minimum && number <= maximum) return number;
        errors.Add(new("simulation.invalid_integer", path + "." + name, "Supply an integer literal within the documented field range."));
        return 0;
    }
}
