using System.Text.Json;
using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Core.Configuration;

internal sealed record GateInterval(long StartTicks, long EndTicks, long ActiveBeforeTicks);

/// <summary>
/// Pure gating from known simulated input. Immutable open intervals preserve
/// exact elapsed time across pauses, including transitions between sample slots.
/// </summary>
/// <param name="PeriodTicks">Set when the trigger timeline repeats: OpenIntervals
/// then describe one cycle and ActivePerCycle is the open time in each cycle.</param>
internal sealed record BooleanGateGeneratorDefinition(
    string TriggerTag, string WhenFalse, GeneratorDefinition Pattern,
    IReadOnlyList<GateInterval>? OpenIntervals = null, long? PeriodTicks = null, long ActivePerCycle = 0) : GeneratorDefinition
{
    internal BooleanGateGeneratorDefinition Bind(GeneratorDefinition source)
    {
        var intervals = new List<GateInterval>();
        if (source is ConstantGeneratorDefinition constant)
        {
            if (constant.Value.GetBoolean()) intervals.Add(new(0, long.MaxValue, 0));
        }
        else if (source is BooleanTimelineGeneratorDefinition { PeriodTicks: { } period } cyclic)
        {
            // One cycle of open intervals. The final step ends at the cycle
            // boundary; a state true on both sides simply continues.
            long active = 0;
            foreach (var step in cyclic.Steps)
            {
                if (!step.Value) continue;
                intervals.Add(new(step.StartTicks, step.EndTicks, active));
                active += step.EndTicks - step.StartTicks;
            }
            return this with { OpenIntervals = intervals.AsReadOnly(), PeriodTicks = period, ActivePerCycle = active };
        }
        else if (source is BooleanTimelineGeneratorDefinition timeline)
        {
            long active = 0;
            for (int i = 0; i < timeline.Steps.Count; i++)
            {
                var step = timeline.Steps[i];
                if (!step.Value) continue;
                // Timeline holdLast extends its final true state through any
                // later session samples, rather than closing at the final dwell.
                long end = i == timeline.Steps.Count - 1 ? long.MaxValue : step.EndTicks;
                intervals.Add(new(step.StartTicks, end, active));
                active += end - step.StartTicks;
            }
        }
        else throw new InvalidOperationException("Unsupported gate source.");
        return this with { OpenIntervals = intervals.AsReadOnly() };
    }

    private (GateInterval Interval, long Cycle, long Local)? OpenAt(long elapsedTicks)
    {
        var intervals = OpenIntervals ?? throw new InvalidOperationException("Gate was not bound during validation.");
        long cycle = 0;
        if (PeriodTicks is { } period)
        {
            cycle = elapsedTicks / period;
            elapsedTicks %= period;
        }
        int low = 0, high = intervals.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (intervals[middle].StartTicks <= elapsedTicks) low = middle + 1;
            else high = middle;
        }
        return low > 0 && elapsedTicks < intervals[low - 1].EndTicks ? (intervals[low - 1], cycle, elapsedTicks) : null;
    }

    internal override bool EmitsAt(long elapsedTicks) => OpenAt(elapsedTicks) is not null;

    internal override JsonElement? Evaluate(long elapsedTicks)
    {
        var (interval, cycle, within) = OpenAt(elapsedTicks) ?? throw new InvalidOperationException("A closed gate must not be evaluated for output.");
        // Both modes initially wait for first true. Later false spans either
        // stop the pattern clock or advance it without creating any TVQ points.
        // A repeating trigger adds the open time of every completed cycle.
        long local = WhenFalse == "pauseAndSuppress"
            ? cycle * ActivePerCycle + interval.ActiveBeforeTicks + (within - interval.StartTicks)
            : elapsedTicks - OpenIntervals![0].StartTicks;
        return Pattern.Evaluate(local);
    }
}

internal static class BooleanGateDefinitionLoader
{
    internal static GeneratorDefinition? Load(JsonElement generator, OutputTagDefinition? output,
        string path, ValidationErrors errors, ref int remainingHolds)
    {
        long before = errors.ErrorCount;
        string? trigger = SessionDefinitionLoader.ReadString(generator, "triggerTag", path, errors);
        string? mode = SessionDefinitionLoader.ReadString(generator, "whenFalse", path, errors);
        if (mode is not null && mode is not ("pauseAndSuppress" or "continueAndSuppress"))
            errors.Add(new("simulation.invalid_gate_mode", path + ".whenFalse", "Use pauseAndSuppress or continueAndSuppress."));
        if (!generator.TryGetProperty("pattern", out var pattern) || pattern.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("simulation.gate_pattern_required", path + ".pattern", "Supply a pattern object with kind constant, ramp, staircase, randomIntegerHold, or sequence."));
            return null;
        }
        var child = SimulationDefinitionLoader.ReadPattern(pattern, output, path + ".pattern", errors,
            ref remainingHolds, gateChild: true);
        return errors.ErrorCount == before ? new BooleanGateGeneratorDefinition(trigger!, mode!, child!) : null;
    }
}
