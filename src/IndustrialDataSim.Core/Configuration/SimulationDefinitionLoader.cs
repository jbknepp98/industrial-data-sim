using System.Collections.ObjectModel;
using System.Text.Json;
using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Core.Configuration;

/// <summary>
/// A deliberately narrow executable format. No implicit generator defaults,
/// coercions, unseeded randomness, or credentials are permitted.
/// </summary>
public static class SimulationDefinitionLoader
{
    public static SimulationLoadResult Load(string json)
    {
        using JsonDocument? document = SessionDefinitionLoader.Parse(json, out var parseError);
        if (parseError is not null)
        {
            return new(null, [parseError]);
        }
        var root = document!.RootElement;
        var errors = new ValidationErrors();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new(null, [new("simulation.object_required", "$", "Expected a simulation object.")]);
        }
        SessionDefinitionLoader.CheckProperties(root,
            ["schemaVersion", "generatorVersion", "session", "samplingIntervalMs", "generators"], "$", errors);
        CheckVersion(root, "schemaVersion", errors);
        CheckVersion(root, "generatorVersion", errors);

        SessionDefinition? session = null;
        if (!root.TryGetProperty("session", out var header))
        {
            errors.Add(new("simulation.required", "$.session", "A session header is required."));
        }
        else
        {
            var result = SessionDefinitionLoader.Load(header.GetRawText());
            session = result.Definition;
            errors.AddRange(result.Errors.Select(error => error with { Path = "$.session" + error.Path[1..] }));
        }

        int interval = 0;
        if (!root.TryGetProperty("samplingIntervalMs", out var sampling) ||
            sampling.ValueKind != JsonValueKind.Number || !sampling.TryGetInt32(out interval) || interval <= 0)
        {
            errors.Add(new("simulation.invalid_interval", "$.samplingIntervalMs",
                "Use a whole-number interval from 1 through 2147483647 milliseconds."));
        }

        int remainingRandomHolds = 10_000;
        var definitions = new Dictionary<string, GeneratorDefinition>(StringComparer.Ordinal);
        if (!root.TryGetProperty("generators", out var generators) ||
            generators.ValueKind != JsonValueKind.Array || generators.GetArrayLength() == 0)
        {
            errors.Add(new("simulation.generators_required", "$.generators", "Expected a nonempty generator array."));
        }
        else
        {
            int index = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outputs = session?.OutputTags.ToDictionary(tag => tag.Name, StringComparer.Ordinal);
            foreach (var generator in generators.EnumerateArray())
            {
                string path = $"$.generators[{index++}]";
                if (generator.ValueKind != JsonValueKind.Object)
                {
                    errors.Add(new("simulation.generator_object_required", path, "Expected a generator object."));
                    continue;
                }
                string? tag = SessionDefinitionLoader.ReadString(generator, "tag", path, errors);
                if (tag is not null && !names.Add(tag))
                {
                    errors.Add(new("simulation.duplicate_generator", path + ".tag", "Each output must have exactly one generator."));
                }
                OutputTagDefinition? output = null;
                if (tag is not null && outputs is not null && !outputs.TryGetValue(tag, out output))
                {
                    errors.Add(new("simulation.unknown_tag", path + ".tag", "Reference a declared output tag with exact spelling."));
                }
                GeneratorDefinition? definition = ReadPattern(generator, output, path, errors, ref remainingRandomHolds);
                if (tag is not null && definition is not null)
                {
                    definitions.TryAdd(tag, definition);
                }
            }
            if (session is not null)
            {
                for (int i = 0; i < session.OutputTags.Count; i++)
                {
                    if (!definitions.ContainsKey(session.OutputTags[i].Name))
                    {
                        errors.Add(new("simulation.missing_generator", $"$.session.outputTags[{i}].name",
                            "Add one generator whose tag matches this output exactly, or correct the reported errors in its existing generator."));
                    }
                }
            }
        }
        // Bind only direct local Boolean sources after all declarations have
        // been parsed. This makes declaration order irrelevant and excludes
        // cycles, external reads, and ambiguous/unavailable input values.
        if (errors.ErrorCount == 0 && session is not null)
        {
            for (int i = 0; i < session.OutputTags.Count; i++)
            {
                string name = session.OutputTags[i].Name;
                string? triggerTag = definitions[name] switch
                {
                    BooleanSwitchGeneratorDefinition choice => choice.TriggerTag,
                    BooleanGateGeneratorDefinition gate => gate.TriggerTag,
                    _ => null
                };
                if (triggerTag is null) continue;
                var sourceTag = session.OutputTags.FirstOrDefault(t => t.Name == triggerTag);
                if (sourceTag?.ValueType != "boolean" || !definitions.TryGetValue(triggerTag, out var source) ||
                    source is not (BooleanTimelineGeneratorDefinition or ConstantGeneratorDefinition))
                {
                    int generatorIndex = 0;
                    foreach (var item in generators.EnumerateArray())
                    {
                        if (item.GetProperty("tag").GetString() == name) break;
                        generatorIndex++;
                    }
                    errors.Add(new("simulation.invalid_trigger", $"$.generators[{generatorIndex}].triggerTag",
                        "Reference an existing local Boolean timeline or Boolean constant tag with exact spelling."));
                }
                else definitions[name] = definitions[name] switch
                {
                    BooleanSwitchGeneratorDefinition choice => choice with { Trigger = source },
                    BooleanGateGeneratorDefinition gate => gate.Bind(source),
                    _ => throw new InvalidOperationException("Unexpected dependency definition.")
                };
            }
        }
        return errors.ErrorCount == 0
            ? new(new(session!, interval, new ReadOnlyDictionary<string, GeneratorDefinition>(definitions)), [])
            : new(null, errors.AsReadOnly());
    }

    internal static GeneratorDefinition? ReadPattern(JsonElement generator, OutputTagDefinition? output,
        string path, ValidationErrors errors, ref int remainingRandomHolds, bool sequenceChild = false, bool switchBranch = false, bool gateChild = false)
    {
        string? kind = SessionDefinitionLoader.ReadString(generator, "kind", path, errors);
        string[] fields = kind switch
        {
            "booleanGate" => ["kind", "triggerTag", "whenFalse", "pattern"],
            "booleanTimeline" => ["kind", "steps", "afterSteps"],
            "booleanSwitch" => ["kind", "triggerTag", "onChange", "whenFalse", "whenTrue"],
            "ramp" => ["kind", "startValue", "ratePerSecond", "minimum", "maximum"],
            "randomIntegerHold" => ["kind", "minimum", "maximum", "seed", "durationMs", "holdDurationMs", "holdDurationRangeMs", "adjacentValues", "afterDuration"],
            "staircase" => ["kind", "steps", "afterSteps", "seed", "maxTotalDurationMs"],
            "sequence" => ["kind", "steps", "afterSequence"],
            _ => ["kind", "value"]
        };
        SessionDefinitionLoader.CheckProperties(generator, (sequenceChild || switchBranch || gateChild) ? fields : ["tag", .. fields], path, errors);
        // Child patterns inherit the owning tag and are limited to finite,
        // already-tested hold schedules. No recursive sequences in this increment.
        if (sequenceChild && kind is not ("staircase" or "randomIntegerHold"))
        {
            errors.Add(new("simulation.unsupported_sequence_pattern", path + ".kind",
                "Sequence steps currently support staircase and randomIntegerHold."));
            return null;
        }
        if (switchBranch && kind != "sequence")
        {
            errors.Add(new("simulation.unsupported_switch_branch", path + ".kind", "Switch branches must be sequences."));
            return null;
        }
        if (gateChild && kind is not ("constant" or "ramp" or "staircase" or "randomIntegerHold" or "sequence"))
        {
            errors.Add(new("simulation.unsupported_gate_pattern", path + ".kind", "Use a constant, ramp, staircase, randomIntegerHold, or sequence."));
            return null;
        }
        switch (kind)
        {
            case "booleanGate": return BooleanGateDefinitionLoader.Load(generator, output, path, errors, ref remainingRandomHolds);
            case "booleanTimeline": return BooleanTriggerDefinitionLoader.LoadTimeline(generator, output, path, errors);
            case "booleanSwitch": return BooleanTriggerDefinitionLoader.LoadSwitch(generator, output, path, errors, ref remainingRandomHolds);
            case "constant": return ReadConstant(generator, output, path, errors);
            case "ramp": return ReadRamp(generator, output, path, errors);
            case "staircase": return StaircaseDefinitionLoader.Load(generator, output, path, errors);
            case "randomIntegerHold": return RandomIntegerHoldDefinitionLoader.Load(generator, output, path, errors, ref remainingRandomHolds);
            case "sequence": return SequenceDefinitionLoader.Load(generator, output, path, errors, ref remainingRandomHolds);
            default:
                if (kind is not null)
                    errors.Add(new("simulation.unsupported_generator", path + ".kind",
                        "Supported generator kinds are constant, ramp, staircase, randomIntegerHold, sequence, booleanTimeline, booleanSwitch, and booleanGate."));
                return null;
        }
    }

    private static GeneratorDefinition? ReadConstant(JsonElement generator, OutputTagDefinition? output,
        string path, ValidationErrors errors)
    {
        if (!generator.TryGetProperty("value", out var value))
        {
            errors.Add(new("simulation.required", path + ".value", "A constant value is required."));
            return null;
        }
        bool scalar = value.ValueKind is JsonValueKind.Number or JsonValueKind.True
            or JsonValueKind.False or JsonValueKind.String;
        bool finite = value.ValueKind != JsonValueKind.Number ||
            (value.TryGetDouble(out double number) && double.IsFinite(number));
        bool matches = output is null || (output.ValueType switch
        {
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "string" => value.ValueKind == JsonValueKind.String,
            _ => false
        });
        if (!scalar || !finite || !matches)
        {
            errors.Add(new("simulation.invalid_constant", path + ".value",
                "Supply a finite number, boolean, or string matching the declared value type; null is unsupported."));
            return null;
        }
        // The clone owns its JSON after the parser is disposed. In particular,
        // large integer constants must not be rounded through a double conversion.
        return new ConstantGeneratorDefinition(value.Clone());
    }

    private static GeneratorDefinition? ReadRamp(JsonElement generator, OutputTagDefinition? output,
        string path, ValidationErrors errors)
    {
        long initialErrors = errors.ErrorCount;
        if (output is not null && output.ValueType != "number")
        {
            errors.Add(new("simulation.ramp_requires_number", path + ".tag",
                "A ramp must target an output declared as number."));
        }
        double? start = ReadFiniteNumber(generator, "startValue", path, required: true, errors);
        double? rate = ReadFiniteNumber(generator, "ratePerSecond", path, required: true, errors);
        double? minimum = ReadFiniteNumber(generator, "minimum", path, required: false, errors);
        double? maximum = ReadFiniteNumber(generator, "maximum", path, required: false, errors);
        if (minimum.HasValue && maximum.HasValue && minimum > maximum)
        {
            errors.Add(new("simulation.invalid_bounds", path + ".maximum",
                "Maximum must be greater than or equal to minimum."));
        }
        if (start.HasValue && ((minimum.HasValue && start < minimum) || (maximum.HasValue && start > maximum)))
        {
            errors.Add(new("simulation.start_outside_bounds", path + ".startValue",
                "Start value must lie within the supplied bounds."));
        }
        return errors.ErrorCount == initialErrors
            ? new RampGeneratorDefinition(start!.Value, rate!.Value, minimum, maximum)
            : null;
    }

    private static double? ReadFiniteNumber(JsonElement generator, string name, string path,
        bool required, ValidationErrors errors)
    {
        if (!generator.TryGetProperty(name, out var value))
        {
            if (required)
            {
                errors.Add(new("simulation.required", path + "." + name, "This numeric field is required."));
            }
            return null;
        }
        // Optional means absent, not null. This keeps accidental nulls from
        // silently removing a process bound in agent-authored configurations.
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number))
        {
            errors.Add(new("simulation.invalid_number", path + "." + name, "Expected a finite JSON number."));
            return null;
        }
        return number;
    }

    private static void CheckVersion(JsonElement root, string name, ValidationErrors errors)
    {
        if (!root.TryGetProperty(name, out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out int value) || value != 1)
        {
            errors.Add(new("simulation.unsupported_version", "$." + name, "Use the supported integer literal 1."));
        }
    }
}
