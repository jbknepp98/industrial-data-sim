using System.Text.Json;
using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Core.Configuration;

internal sealed record BooleanTimelineStep(long StartTicks, long EndTicks, bool Value);

internal sealed record BooleanTimelineGeneratorDefinition(IReadOnlyList<BooleanTimelineStep> Steps) : GeneratorDefinition
{
    internal (bool Value, long SinceTicks) StateAt(long elapsedTicks)
    {
        int low = 0, high = Steps.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (elapsedTicks < Steps[middle].EndTicks) high = middle;
            else low = middle + 1;
        }
        var step = Steps[Math.Min(low, Steps.Count - 1)];
        return (step.Value, step.StartTicks);
    }

    internal override JsonElement? Evaluate(long elapsedTicks) => JsonSerializer.SerializeToElement(StateAt(elapsedTicks).Value);
}

internal sealed record BooleanSwitchGeneratorDefinition(
    string TriggerTag, SequenceGeneratorDefinition WhenFalse, SequenceGeneratorDefinition WhenTrue,
    GeneratorDefinition? Trigger = null) : GeneratorDefinition
{
    internal override JsonElement? Evaluate(long elapsedTicks)
    {
        // Binding validates a direct Boolean timeline/constant dependency. State
        // and activation time come from that same source, not output tag order or
        // a previous sample. Even transitions between samples reset the clock.
        (bool value, long since) = Trigger switch
        {
            BooleanTimelineGeneratorDefinition timeline => timeline.StateAt(elapsedTicks),
            ConstantGeneratorDefinition constant => (constant.Value.GetBoolean(), 0L),
            _ => throw new InvalidOperationException("Boolean trigger was not bound during validation.")
        };
        return (value ? WhenTrue : WhenFalse).Evaluate(elapsedTicks - since);
    }
}

internal static class BooleanTriggerDefinitionLoader
{
    internal static GeneratorDefinition? LoadTimeline(JsonElement generator, OutputTagDefinition? output,
        string path, ValidationErrors errors)
    {
        long before = errors.ErrorCount;
        if (output is not null && output.ValueType != "boolean")
            errors.Add(new("simulation.timeline_requires_boolean", path + ".tag", "Declare the timeline output as boolean."));
        string? after = SessionDefinitionLoader.ReadString(generator, "afterSteps", path, errors);
        if (after is not null && after != "holdLast")
            errors.Add(new("simulation.invalid_after_steps", path + ".afterSteps", "Use holdLast."));
        if (!generator.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array ||
            steps.GetArrayLength() == 0 || steps.GetArrayLength() > 1000)
        {
            errors.Add(new("simulation.invalid_boolean_steps", path + ".steps", "Supply 1 through 1000 Boolean timeline steps."));
            return null;
        }
        long total = 0;
        int index = 0;
        var resolved = new List<BooleanTimelineStep>();
        foreach (var step in steps.EnumerateArray())
        {
            string stepPath = $"{path}.steps[{index++}]";
            if (step.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new("simulation.boolean_step_object_required", stepPath, "Expected a Boolean timeline step."));
                continue;
            }
            SessionDefinitionLoader.CheckProperties(step, ["value", "durationMs"], stepPath, errors);
            if (!step.TryGetProperty("value", out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                errors.Add(new("simulation.invalid_boolean", stepPath + ".value", "Use a JSON Boolean, not a string or number."));
                continue;
            }
            if (!step.TryGetProperty("durationMs", out var duration) || duration.ValueKind != JsonValueKind.Number ||
                !duration.TryGetInt64(out long ms) || ms <= 0 || ms > (long.MaxValue - total) / TimeSpan.TicksPerMillisecond)
            {
                errors.Add(new("simulation.invalid_step_duration", stepPath + ".durationMs", "Use positive integer milliseconds within the cumulative tick limit."));
                continue;
            }
            long end = total + ms * TimeSpan.TicksPerMillisecond;
            bool state = value.GetBoolean();
            // Consecutive equal states are one activation, not repeated triggers.
            if (resolved.Count > 0 && resolved[^1].Value == state)
                resolved[^1] = resolved[^1] with { EndTicks = end };
            else resolved.Add(new(total, end, state));
            total = end;
        }
        return errors.ErrorCount == before ? new BooleanTimelineGeneratorDefinition(resolved.AsReadOnly()) : null;
    }

    internal static GeneratorDefinition? LoadSwitch(JsonElement generator, OutputTagDefinition? output,
        string path, ValidationErrors errors, ref int remainingHolds)
    {
        long before = errors.ErrorCount;
        if (output is not null && output.ValueType != "number")
            errors.Add(new("simulation.switch_requires_number", path + ".tag", "Declare the switched output as number."));
        string? trigger = SessionDefinitionLoader.ReadString(generator, "triggerTag", path, errors);
        string? onChange = SessionDefinitionLoader.ReadString(generator, "onChange", path, errors);
        if (onChange is not null && onChange != "restartBranch")
            errors.Add(new("simulation.invalid_on_change", path + ".onChange", "Use restartBranch."));
        var whenFalse = ReadBranch(generator, "whenFalse", output, path, errors, ref remainingHolds);
        var whenTrue = ReadBranch(generator, "whenTrue", output, path, errors, ref remainingHolds);
        return errors.ErrorCount == before ? new BooleanSwitchGeneratorDefinition(trigger!, whenFalse!, whenTrue!) : null;
    }

    private static SequenceGeneratorDefinition? ReadBranch(JsonElement generator, string name,
        OutputTagDefinition? output, string path, ValidationErrors errors, ref int remainingHolds)
    {
        if (!generator.TryGetProperty(name, out var branch) || branch.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("simulation.branch_required", path + "." + name, "Supply a sequence branch."));
            return null;
        }
        return SimulationDefinitionLoader.ReadPattern(branch, output, path + "." + name, errors,
            ref remainingHolds, switchBranch: true) as SequenceGeneratorDefinition;
    }
}
