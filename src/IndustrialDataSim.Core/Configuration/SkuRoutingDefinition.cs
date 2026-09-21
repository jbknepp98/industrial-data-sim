using System.Text.Json;
using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Core.Configuration;

internal sealed record StringTimelineStep(long StartTicks, long EndTicks, string Value);
internal sealed record StringTimelineGeneratorDefinition(IReadOnlyList<StringTimelineStep> Steps) : GeneratorDefinition
{
    internal string ValueAt(long elapsedTicks)
    {
        int low = 0, high = Steps.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (elapsedTicks < Steps[middle].EndTicks) high = middle;
            else low = middle + 1;
        }
        return Steps[Math.Min(low, Steps.Count - 1)].Value;
    }
    internal override JsonElement? Evaluate(long elapsedTicks) => JsonSerializer.SerializeToElement(ValueAt(elapsedTicks));
}

// A route is compiled from exact source boundaries before generation. Gates
// consume that same Boolean schedule, including transitions between samples.
internal sealed record SkuRouteGeneratorDefinition(string SkuTag, string ReadyTag, string Sku) : GeneratorDefinition
{
    internal override JsonElement? Evaluate(long elapsedTicks) => throw new InvalidOperationException("SKU route was not bound during validation.");
}

internal static class SkuRoutingDefinitionLoader
{
    internal static GeneratorDefinition? LoadTimeline(JsonElement generator, OutputTagDefinition? output, string path, ValidationErrors errors)
    {
        long before = errors.ErrorCount;
        if (output is not null && output.ValueType != "string")
            errors.Add(new("simulation.string_timeline_requires_string", path + ".tag", "Declare the string timeline output as string."));
        string? after = SessionDefinitionLoader.ReadString(generator, "afterSteps", path, errors);
        if (after is not null && after != "holdLast")
            errors.Add(new("simulation.invalid_after_steps", path + ".afterSteps", "Use holdLast."));
        if (!generator.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() is < 1 or > 1000)
        {
            errors.Add(new("simulation.invalid_string_steps", path + ".steps", "Supply 1 through 1000 string timeline steps."));
            return null;
        }
        long total = 0;
        int index = 0;
        var resolved = new List<StringTimelineStep>();
        foreach (var step in steps.EnumerateArray())
        {
            string location = $"{path}.steps[{index++}]";
            if (step.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new("simulation.string_step_object_required", location, "Supply a string timeline step object with value and durationMs."));
                continue;
            }
            SessionDefinitionLoader.CheckProperties(step, ["value", "durationMs"], location, errors);
            string? value = SessionDefinitionLoader.ReadString(step, "value", location, errors);
            if (!step.TryGetProperty("durationMs", out var duration) || duration.ValueKind != JsonValueKind.Number ||
                !duration.TryGetInt64(out long ms) || ms <= 0 || ms > (long.MaxValue - total) / TimeSpan.TicksPerMillisecond)
            {
                errors.Add(new("simulation.invalid_step_duration", location + ".durationMs", "Supply a positive integer duration in milliseconds within the remaining Int64 tick range. Shorten earlier steps if necessary."));
                continue;
            }
            long end = total + ms * TimeSpan.TicksPerMillisecond;
            if (value is not null) resolved.Add(new(total, end, value));
            total = end;
        }
        return errors.ErrorCount == before ? new StringTimelineGeneratorDefinition(resolved.AsReadOnly()) : null;
    }

    internal static GeneratorDefinition? LoadRoute(JsonElement generator, OutputTagDefinition? output, string path, ValidationErrors errors)
    {
        long before = errors.ErrorCount;
        if (output is not null && output.ValueType != "boolean")
            errors.Add(new("simulation.route_requires_boolean", path + ".tag", "Declare the SKU route output as boolean."));
        string? skuTag = SessionDefinitionLoader.ReadString(generator, "skuTag", path, errors);
        string? readyTag = SessionDefinitionLoader.ReadString(generator, "readyTag", path, errors);
        string? sku = SessionDefinitionLoader.ReadString(generator, "sku", path, errors);
        return errors.ErrorCount == before ? new SkuRouteGeneratorDefinition(skuTag!, readyTag!, sku!) : null;
    }

    internal static void BindRoutes(Dictionary<string, GeneratorDefinition> definitions, JsonElement generators, ValidationErrors errors)
    {
        var sources = new Dictionary<string, GeneratorDefinition>(definitions, StringComparer.Ordinal);
        var claims = new HashSet<(string, string, string)>();
        int index = 0;
        foreach (var generator in generators.EnumerateArray())
        {
            string path = $"$.generators[{index++}]";
            string name = generator.GetProperty("tag").GetString()!;
            if (definitions[name] is not SkuRouteGeneratorDefinition route) continue;
            if (!sources.TryGetValue(route.SkuTag, out var skuSource) || skuSource is not StringTimelineGeneratorDefinition sku)
            {
                errors.Add(new("simulation.invalid_sku_source", path + ".skuTag", "Reference a local stringTimeline tag with exact spelling. Routes cannot depend on other routes or external tags."));
                continue;
            }
            if (!sources.TryGetValue(route.ReadyTag, out var readySource) ||
                readySource is not BooleanTimelineGeneratorDefinition && readySource is not ConstantGeneratorDefinition { Value.ValueKind: JsonValueKind.True or JsonValueKind.False })
            {
                errors.Add(new("simulation.invalid_ready_source", path + ".readyTag", "Reference a local Boolean timeline or Boolean constant with exact spelling."));
                continue;
            }
            if (!claims.Add((route.SkuTag, route.ReadyTag, route.Sku)))
            {
                errors.Add(new("simulation.duplicate_sku_route", path + ".sku", "Only one route may claim a SKU for the same SKU and readiness sources. Assign distinct SKU values to the cells."));
                continue;
            }
            // Union the source transitions. There are at most 2000 intervals;
            // the final interval holds indefinitely, just like either source.
            var boundaries = new SortedSet<long>(sku.Steps.Select(s => s.StartTicks)) { 0 };
            if (readySource is BooleanTimelineGeneratorDefinition timeline)
                boundaries.UnionWith(timeline.Steps.Select(s => s.StartTicks));
            var starts = boundaries.ToArray();
            var steps = new List<BooleanTimelineStep>();
            for (int i = 0; i < starts.Length; i++)
            {
                long start = starts[i], end = i + 1 < starts.Length ? starts[i + 1] : long.MaxValue;
                bool ready = readySource is BooleanTimelineGeneratorDefinition source ? source.StateAt(start).Value : ((ConstantGeneratorDefinition)readySource).Value.GetBoolean();
                bool active = ready && string.Equals(sku.ValueAt(start), route.Sku, StringComparison.Ordinal);
                if (steps.Count > 0 && steps[^1].Value == active) steps[^1] = steps[^1] with { EndTicks = end };
                else steps.Add(new(start, end, active));
            }
            definitions[name] = new BooleanTimelineGeneratorDefinition(steps.AsReadOnly());
        }
    }
}
