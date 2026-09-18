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
        string? after = SessionDefinitionLoader.ReadString(generator, "afterSequence", path, errors);
        if (after is not null && after != "holdLast")
            errors.Add(new("simulation.invalid_after_sequence", path + ".afterSequence", "Use holdLast."));
        if (!generator.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array ||
            steps.GetArrayLength() == 0 || steps.GetArrayLength() > 1000)
        {
            errors.Add(new("simulation.invalid_sequence_steps", path + ".steps", "Supply 1 through 1000 sequence steps."));
            return null;
        }
        var resolved = new List<SequenceStep>();
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
                errors.Add(new("simulation.sequence_pattern_required", stepPath + ".pattern", "Expected a pattern object."));
                continue;
            }
            var child = SimulationDefinitionLoader.ReadPattern(pattern, output, stepPath + ".pattern", errors,
                ref remainingRandomHolds, sequenceChild: true);
            if (child is not StaircaseGeneratorDefinition schedule) continue;
            // Both supported patterns resolve to finite hold schedules. A child's
            // standalone holdLast policy does not prevent sequence completion.
            long duration = schedule.Steps[^1].EndTicks;
            if (duration > long.MaxValue - elapsed)
            {
                errors.Add(new("simulation.sequence_duration_overflow", stepPath, "Combined sequence duration exceeds the supported tick range."));
                continue;
            }
            resolved.Add(new(elapsed, elapsed + duration, child));
            elapsed += duration;
        }
        return errors.ErrorCount == initialErrors ? new SequenceGeneratorDefinition(resolved.AsReadOnly()) : null;
    }
}
