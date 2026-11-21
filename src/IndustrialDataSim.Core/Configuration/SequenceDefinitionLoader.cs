using System.Text.Json;
using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Core.Configuration;

/// <summary>Compose finite schedules; each child runs on its own local clock.</summary>
internal static class SequenceDefinitionLoader
{
    internal static GeneratorDefinition? Load(JsonElement generator, OutputTagDefinition? output,
        string path, ValidationErrors errors, ref int remainingRandomHolds)
    {
        long initialErrors = errors.ErrorCount;
        if (output is not null && output.ValueType != "number")
            errors.Add(new("simulation.sequence_requires_number", path + ".tag", "Declare this sequence output as number."));
        string? after = SessionDefinitionLoader.ReadAfterPolicy(generator, "afterSequence", path, errors);
        if (!generator.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array ||
            steps.GetArrayLength() == 0 || steps.GetArrayLength() > 1000)
        {
            errors.Add(new("simulation.invalid_sequence_steps", path + ".steps", "Supply 1 through 1000 sequence steps."));
            return null;
        }
        var resolved = new List<SequenceStep>();
        var children = new List<(StaircaseGeneratorDefinition Schedule, string Path)>();
        long elapsed = 0;
        int index = 0;
        foreach (var step in steps.EnumerateArray())
        {
            string stepPath = $"{path}.steps[{index++}]";
            if (step.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new("simulation.sequence_step_object_required", stepPath, "Expected a sequence step object."));
                continue;
            }
            SessionDefinitionLoader.CheckProperties(step, ["pattern"], stepPath, errors);
            if (!step.TryGetProperty("pattern", out var pattern) || pattern.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new("simulation.sequence_pattern_required", stepPath + ".pattern", "Supply a pattern object with kind staircase or randomIntegerHold."));
                continue;
            }
            var child = SimulationDefinitionLoader.ReadPattern(pattern, output, stepPath + ".pattern", errors,
                ref remainingRandomHolds, sequenceChild: true);
            if (child is not StaircaseGeneratorDefinition schedule) continue;
            children.Add((schedule, stepPath + ".pattern"));
            // Both supported patterns resolve to finite hold schedules. A child's
            // standalone holdLast policy does not prevent sequence completion.
            long duration = schedule.Steps[^1].EndTicks;
            if (duration > long.MaxValue - elapsed)
            {
                errors.Add(new("simulation.sequence_duration_overflow", stepPath, "Combined sequence duration exceeds 922337203685477 milliseconds. Shorten or remove steps."));
                continue;
            }
            resolved.Add(new(elapsed, elapsed + duration, child));
            elapsed += duration;
        }
        if (errors.ErrorCount != initialErrors) return null;
        if (after != "repeat") return new SequenceGeneratorDefinition(resolved.AsReadOnly());

        // A repeating sequence needs a fixed cycle: each step occupies its own
        // fixed cycle length (fixed durations, maxTotalDurationMs or durationMs).
        long period = 0;
        foreach (var (schedule, childPath) in children)
        {
            if (schedule.CycleTicks is not { } length)
            {
                errors.Add(new("simulation.repeat_needs_fixed_cycle", childPath + ".maxTotalDurationMs",
                    "A step in a repeating sequence needs a fixed length. Add maxTotalDurationMs to this staircase, or use afterSequence holdLast."));
                continue;
            }
            if (length > long.MaxValue - period)
            {
                errors.Add(new("simulation.sequence_duration_overflow", childPath, "Combined sequence cycle exceeds 922337203685477 milliseconds. Shorten or remove steps."));
                continue;
            }
            period += length;
        }
        if (errors.ErrorCount != initialErrors) return null;
        SequenceGeneratorDefinition Plan(long cycle)
        {
            var steps = new List<SequenceStep>(children.Count);
            long start = 0;
            foreach (var (schedule, _) in children)
            {
                var planned = cycle == 0 ? schedule : schedule.Replan!(cycle);
                steps.Add(new(start, start + schedule.CycleTicks!.Value, planned));
                start += schedule.CycleTicks!.Value;
            }
            return new SequenceGeneratorDefinition(steps.AsReadOnly());
        }
        return new RepeatingScheduleGeneratorDefinition(period, Plan);
    }
}
