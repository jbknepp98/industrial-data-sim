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
