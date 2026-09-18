using System.Text.Json;
using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Core.Configuration;

/// <summary>
/// Resolve timing once at validation, independently of sampling and other tags.
/// Execution then uses the same immutable schedule as a fixed staircase.
/// </summary>
internal static class StaircaseDefinitionLoader
{
    private const long MaximumDurationMs = long.MaxValue / TimeSpan.TicksPerMillisecond;
    private sealed record Step(JsonElement Value, long MinimumMs, long MaximumMs);

    internal static GeneratorDefinition? Load(JsonElement generator, OutputTagDefinition? output,
        string path, ValidationErrors errors)
    {
        long initialErrors = errors.ErrorCount;
        if (output is not null && output.ValueType != "number")
        {
            errors.Add(new("simulation.staircase_requires_number", path + ".tag",
                "A staircase must target an output declared as number."));
        }
        string? afterSteps = SessionDefinitionLoader.ReadString(generator, "afterSteps", path, errors);
        if (afterSteps is not null && afterSteps != "holdLast")
        {
            errors.Add(new("simulation.invalid_after_steps", path + ".afterSteps", "Use holdLast."));
        }
        long budget = MaximumDurationMs;
        if (generator.TryGetProperty("maxTotalDurationMs", out var limit))
        {
            budget = ReadDuration(limit, path + ".maxTotalDurationMs", errors);
        }
        uint seed = 0;
        bool hasSeed = generator.TryGetProperty("seed", out var seedElement);
        if (hasSeed && (seedElement.ValueKind != JsonValueKind.Number || !seedElement.TryGetUInt32(out seed)))
        {
            errors.Add(new("simulation.invalid_seed", path + ".seed",
                "Use an integer literal from 0 through 4294967295."));
        }
        if (!generator.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() == 0)
        {
            errors.Add(new("simulation.steps_required", path + ".steps", "Expected a nonempty step array."));
            return null;
        }
        var parsed = new List<Step>();
        bool hasRange = false;
        long minimumTotal = 0;
        int index = 0;
        foreach (var step in steps.EnumerateArray())
        {
            string stepPath = $"{path}.steps[{index++}]";
            long beforeStep = errors.ErrorCount;
            if (step.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new("simulation.step_object_required", stepPath, "Expected a step object."));
                continue;
            }
            SessionDefinitionLoader.CheckProperties(step, ["value", "durationMs", "durationRangeMs"], stepPath, errors);
            if (!step.TryGetProperty("value", out var value))
            {
                errors.Add(new("simulation.required", stepPath + ".value", "A numeric step value is required."));
            }
            else if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number))
            {
                errors.Add(new("simulation.invalid_number", stepPath + ".value", "Expected a finite JSON number."));
            }
            bool fixedDuration = step.TryGetProperty("durationMs", out var duration);
            bool rangedDuration = step.TryGetProperty("durationRangeMs", out var range);
            hasRange |= rangedDuration;
            long minimum = 0;
            long maximum = 0;
            if (fixedDuration == rangedDuration)
            {
                errors.Add(new("simulation.invalid_step_duration", stepPath + ".durationMs",
                    "Supply exactly one of durationMs or durationRangeMs."));
            }
            else if (fixedDuration)
            {
                minimum = maximum = ReadDuration(duration, stepPath + ".durationMs", errors);
            }
            else if (range.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new("simulation.invalid_duration_range", stepPath + ".durationRangeMs",
                    "Expected an object with minimum and maximum durations."));
            }
            else
            {
                string rangePath = stepPath + ".durationRangeMs";
                SessionDefinitionLoader.CheckProperties(range, ["minimum", "maximum"], rangePath, errors);
                range.TryGetProperty("minimum", out var low);
                range.TryGetProperty("maximum", out var high);
                minimum = ReadDuration(low, rangePath + ".minimum", errors);
                maximum = ReadDuration(high, rangePath + ".maximum", errors);
                if (minimum > maximum)
                {
                    errors.Add(new("simulation.invalid_duration_range", rangePath,
                        "Maximum duration must be greater than or equal to minimum."));
                }
            }
            if (errors.ErrorCount != beforeStep) continue;
            // Sum minima before drawing any durations. Reject impossible plans
            // deterministically, not only for seeds that happen to overflow.
            if (minimum > MaximumDurationMs - minimumTotal)
            {
                errors.Add(new("simulation.invalid_step_duration", stepPath,
                    "The sum of minimum durations exceeds the supported tick range."));
                continue;
            }
            minimumTotal += minimum;
            parsed.Add(new(value.Clone(), minimum, maximum));
        }
        if (hasRange && !hasSeed)
        {
            errors.Add(new("simulation.seed_required", path + ".seed", "Random duration ranges require an explicit seed."));
        }
        if (hasSeed && !hasRange)
        {
            errors.Add(new("simulation.unused_seed", path + ".seed", "A seed requires at least one durationRangeMs step."));
        }
        if (minimumTotal > budget)
        {
            errors.Add(new("simulation.infeasible_duration_limit", path + ".maxTotalDurationMs",
                "The duration limit cannot fit all fixed durations and minimum random durations."));
        }
        if (errors.ErrorCount != initialErrors) return null;

        var resolved = new List<StaircaseStep>(parsed.Count);
        long elapsedMs = 0;
        long remainingMinimum = minimumTotal;
        for (int i = 0; i < parsed.Count; i++)
        {
            var step = parsed[i];
            remainingMinimum -= step.MinimumMs;
            // Reserve every later minimum before choosing this dwell. This is
            // uniform within the current feasible integer range, not uniform
            // across all possible schedules. Earlier steps get first choice.
            long upper = Math.Min(step.MaximumMs, budget - elapsedMs - remainingMinimum);
            long dwell = StableDurationChoice.Choose(seed, i, step.MinimumMs, upper);
            elapsedMs += dwell;
            resolved.Add(new(elapsedMs * TimeSpan.TicksPerMillisecond, step.Value));
        }
        return new StaircaseGeneratorDefinition(resolved.AsReadOnly());
    }

    private static long ReadDuration(JsonElement value, string path, ValidationErrors errors)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long duration) &&
            duration > 0 && duration <= MaximumDurationMs) return duration;
        errors.Add(new("simulation.invalid_step_duration", path,
            "Use positive integer-literal milliseconds no greater than 922337203685477."));
        return 0;
    }
}
