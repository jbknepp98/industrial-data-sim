using System.Collections.ObjectModel;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Core.Simulation;

public sealed record DryRunResult(
    IReadOnlyDictionary<string, IReadOnlyList<TvqPoint>>? Data,
    int PointCount,
    IReadOnlyList<ValidationError> Errors);

/// <summary>
/// Pure bounded generation: no wall clock, network, sleeping, mutable shared
/// state, or writes. The same definition yields the same points each time.
/// </summary>
public static class DryRun
{
    public const int MaximumPoints = 10_000;

    public static DryRunResult Generate(SimulationDefinition definition)
    {
        var session = definition.Session;
        long intervalTicks = definition.SamplingIntervalMs * TimeSpan.TicksPerMillisecond;
        long durationTicks = (session.EndUtc - session.StartUtc).Ticks;
        // Ceiling division for [start,end). Do not use floating-point seconds:
        // they can drift or put a point exactly on the exclusive end boundary.
        long samplesPerTag = (durationTicks - 1) / intervalTicks + 1;
        if (samplesPerTag > MaximumPoints / session.OutputTags.Count)
        {
            return new(null, 0, [new("dry_run.point_limit", "$",
                "Dry-run exceeds 10000 candidate sample slots. Shorten the range or increase the sampling interval.")]);
        }

        var data = new Dictionary<string, IReadOnlyList<TvqPoint>>(StringComparer.Ordinal);
        for (int tagIndex = 0; tagIndex < session.OutputTags.Count; tagIndex++)
        {
            var tag = session.OutputTags[tagIndex];
            var generator = definition.Generators[tag.Name];
            var points = new List<TvqPoint>((int)samplesPerTag);
            for (long index = 0; index < samplesPerTag; index++)
            {
                // Every offset is below durationTicks. Derive from the start
                // rather than adding once after the final point, which could
                // overflow at DateTime's upper boundary unnecessarily.
                long elapsedTicks = index * intervalTicks;
                // Suppressed slots produce no point, placeholder, or buffered payload.
                // Check before evaluating: arithmetic errors remain distinct from suppression.
                if (!generator.EmitsAt(elapsedTicks)) continue;
                DateTime timestamp = session.StartUtc.UtcDateTime.AddTicks(elapsedTicks);
                var value = generator.Evaluate(elapsedTicks);
                if (value is null)
                {
                    // No partial result escapes: other tags/points accumulated
                    // so far are discarded. The CLI has not printed any data.
                    return new(null, 0, [new("dry_run.non_finite_value",
                        $"$.session.outputTags[{tagIndex}].name",
                        "Generator arithmetic produced a non-finite value. Reduce the range, start value, or rate.")]);
                }
                points.Add(new(timestamp, value.Value, 192));
            }
            data.Add(tag.Name, points.AsReadOnly());
        }
        return new(new ReadOnlyDictionary<string, IReadOnlyList<TvqPoint>>(data),
            data.Values.Sum(points => points.Count), []);
    }
}
