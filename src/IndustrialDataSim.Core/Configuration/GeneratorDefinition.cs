using System.Text.Json;

namespace IndustrialDataSim.Core.Configuration;

/// <summary>
/// Immutable, validated generator behavior. Evaluation takes elapsed simulation
/// ticks, never a mutable sample counter or wall clock, so changes in sampling
/// frequency do not change the value at a shared timestamp.
/// </summary>
public abstract record GeneratorDefinition
{
    /// <summary>Whether this timestamp produces a point; suppression is not a null value.</summary>
    internal virtual bool EmitsAt(long elapsedTicks) => true;
    internal virtual int QualityAt(long elapsedTicks) => 192;

    /// <summary>Call only when EmitsAt is true. Null means arithmetic failed; callers must not emit a point.</summary>
    internal abstract JsonElement? Evaluate(long elapsedTicks);
}

internal sealed record ConstantGeneratorDefinition(JsonElement Value) : GeneratorDefinition
{
    internal override JsonElement? Evaluate(long elapsedTicks) => Value;
}

internal sealed record RampGeneratorDefinition(
    double StartValue, double RatePerSecond, double? Minimum, double? Maximum) : GeneratorDefinition
{
    internal override JsonElement? Evaluate(long elapsedTicks)
    {
        // Compute from the origin rather than repeatedly adding increments.
        // Ramps use binary64 arithmetic; constants retain their original JSON.
        double elapsedSeconds = elapsedTicks / (double)TimeSpan.TicksPerSecond;
        double value = StartValue + RatePerSecond * elapsedSeconds;
        if (!double.IsFinite(value))
        {
            // Bounds are a process-model rule, not a way to hide overflow. Fail
            // before clamping instead of turning Infinity into a plausible value.
            return null;
        }
        value = Math.Clamp(value, Minimum ?? double.MinValue, Maximum ?? double.MaxValue);
        return JsonSerializer.SerializeToElement(value);
    }
}

internal sealed record StaircaseStep(long EndTicks, JsonElement Value);

internal sealed record StaircaseGeneratorDefinition(
    IReadOnlyList<StaircaseStep> Steps) : GeneratorDefinition
{
    /// <summary>Fixed cycle length when the schedule can repeat, otherwise null.</summary>
    internal long? CycleTicks { get; init; }
    /// <summary>The model asked for afterSteps/afterDuration: repeat.</summary>
    internal bool Repeat { get; init; }
    /// <summary>Resolves the same schedule for a later cycle with that cycle's own random draws.</summary>
    internal Func<long, StaircaseGeneratorDefinition>? Replan { get; init; }

    internal override JsonElement? Evaluate(long elapsedTicks)
    {
        // Half-open dwell intervals: an exact boundary belongs to the next
        // step. Binary search also skips steps shorter than the sample interval
        // without inventing extra timestamps or carrying mutable cursor state.
        int low = 0;
        int high = Steps.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (elapsedTicks < Steps[middle].EndTicks) high = middle;
            else low = middle + 1;
        }
        // The validated holdLast policy continues through the session end.
        return Steps[Math.Min(low, Steps.Count - 1)].Value;
    }
}

internal sealed record SequenceStep(long StartTicks, long EndTicks, GeneratorDefinition Pattern);

internal sealed record SequenceGeneratorDefinition(IReadOnlyList<SequenceStep> Steps) : GeneratorDefinition
{
    internal override JsonElement? Evaluate(long elapsedTicks)
    {
        int low = 0;
        int high = Steps.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (elapsedTicks < Steps[middle].EndTicks) high = middle;
            else low = middle + 1;
        }
        var step = Steps[Math.Min(low, Steps.Count - 1)];
        // Exactly on a boundary, lookup selects the next pattern at local zero.
        // After the final boundary, freeze the last scheduled value independent
        // of sampling frequency: use the last tick, not the last sampled point.
        long localTicks = Math.Min(elapsedTicks, step.EndTicks - 1) - step.StartTicks;
        return step.Pattern.Evaluate(localTicks);
    }
}

/// <summary>
/// A finite schedule repeated on a fixed cycle. Cycle 0 is the originally
/// resolved schedule; each later cycle is planned from its own deterministic
/// random stream, so values are reproducible from elapsed time alone and do not
/// depend on sampling, window size or restarts.
/// </summary>
internal sealed record RepeatingScheduleGeneratorDefinition(
    long PeriodTicks, Func<long, GeneratorDefinition> Plan) : GeneratorDefinition
{
    // Generation walks forward in time, so one cached cycle avoids replanning
    // every sample. The cache only memoizes the pure Plan function.
    private readonly object cacheLock = new();
    private long cachedCycle = -1;
    private GeneratorDefinition? cachedPlan;

    internal GeneratorDefinition PlanFor(long cycle)
    {
        lock (cacheLock)
        {
            if (cycle != cachedCycle || cachedPlan is null)
            {
                cachedPlan = Plan(cycle);
                cachedCycle = cycle;
            }
            return cachedPlan;
        }
    }

    internal override JsonElement? Evaluate(long elapsedTicks) =>
        PlanFor(elapsedTicks / PeriodTicks).Evaluate(elapsedTicks % PeriodTicks);
}

internal static class CycleStream
{
    /// <summary>
    /// Random stream name for a repeat cycle. Cycle 0 keeps the original
    /// generatorVersion 1 stream, so a repeating pattern's first cycle is
    /// identical to the same pattern with holdLast.
    /// </summary>
    internal static string For(string stream, long cycle) =>
        cycle == 0 ? stream : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{stream}#cycle{cycle}");
}
