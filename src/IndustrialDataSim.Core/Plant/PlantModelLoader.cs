using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Core.Plant;

public sealed record PlantLoadResult(PlantModel? Model, IReadOnlyList<ValidationError> Errors)
{
    public bool IsValid => Model is not null && Errors.Count == 0;
}

/// <summary>
/// Strict loader for plant model files (schemaVersion 1, kind "plant"). Unknown
/// fields are rejected, every reference is checked, and size limits keep demo
/// plants small (owner decision: at most two lines, minimal staff). The anchor
/// ("now") is supplied by the caller: relative backfill and incident times are
/// resolved against it once, so a given anchor always yields the same plant.
/// </summary>
public static partial class PlantModelLoader
{
    // Owner decision, November 21, 2026: keep plants small for now.
    internal const int MaximumLines = 2;
    internal const int MaximumEquipmentPerLine = 6;
    internal const int MaximumSkus = 6;
    internal const int MaximumOperators = 8;
    internal const int MaximumTechnicians = 4;
    internal const int MaximumCustomers = 6;
    internal const int MaximumParts = 20;
    internal const int MaximumFailureModes = 12;
    internal const int MaximumPreventive = 6;
    internal const int MaximumIncidents = 10;

    public static PlantLoadResult Load(string json, DateTimeOffset anchorUtc)
    {
        using var document = SessionDefinitionLoader.Parse(json, out var parseError);
        if (parseError is not null) return new(null, [parseError]);
        var errors = new ValidationErrors();
        var root = document!.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return new(null, [new("plant.object_required", "$", "The plant model must be a JSON object.")]);
        SessionDefinitionLoader.CheckProperties(root, ["schemaVersion", "kind", "simulation", "seed", "timeline", "sampleIntervalMs",
            "tagRoot", "shifts", "skus", "lines", "people", "customers", "parts", "demand", "maintenance", "incidents", "quality", "shipping"], "$", errors);
        if (!root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int v) || v != 1)
            errors.Add(new("plant.unsupported_version", "$.schemaVersion", "Use schemaVersion 1 (integer literal)."));
        if (Text(root, "kind", "$", errors) is { } kind && kind != "plant")
            errors.Add(new("plant.invalid_kind", "$.kind", "Use \"plant\" for plant models. Tag-only models use the simulation format instead."));
        string? simulation = Text(root, "simulation", "$", errors);
        if (simulation is not null && !SimulationName().IsMatch(simulation))
            errors.Add(new("plant.invalid_simulation", "$.simulation", "Use 2–32 letters or digits starting with an upper-case letter, for example BluebirdBev. It names the tag root and, in lower case, prefixes every table."));
        ulong seed = 0;
        if (!root.TryGetProperty("seed", out var seedElement) || seedElement.ValueKind != JsonValueKind.Number || !seedElement.TryGetUInt64(out seed))
            errors.Add(new("plant.invalid_seed", "$.seed", "Supply a non-negative integer seed. The same seed and anchor always reproduce the same plant history."));
        int sample = Integer(root, "sampleIntervalMs", "$", 1000, 60000, errors);
        string tagRoot = root.TryGetProperty("tagRoot", out _) ? Text(root, "tagRoot", "$", errors) ?? "" : simulation ?? "";
        if (tagRoot.Length > 0 && !TagRoot().IsMatch(tagRoot))
            errors.Add(new("plant.invalid_tag_root", "$.tagRoot", "Use letters, digits, dots, hyphens or underscores (1–64), starting with a letter."));
        var (start, end) = Timeline(root, anchorUtc, errors);

        var shifts = Shifts(root, errors);
        var skus = Skus(root, errors);
        var lines = Lines(root, skus, errors);
        foreach (var (sku, index) in skus.Select((s, n) => (s, n)))
            if (lines.Count > 0 && lines.All(l => !l.Skus.Contains(sku.Code)))
                errors.Add(new("plant.unmade_sku", $"$.skus[{index}]", "No line can make this SKU, so its orders could never be produced. Add it to a line's skus or remove it."));
        var (operators, technicians) = People(root, shifts, lines, errors);
        var customers = List(root, "customers", "$", 1, MaximumCustomers, errors, (item, path) =>
        {
            SessionDefinitionLoader.CheckProperties(item, ["id", "name"], path, errors);
            return new PlantCustomer(Id(item, "id", path, errors) ?? "", Text(item, "name", path, errors) ?? "");
        });
        Unique(customers.Select(c => c.Id), "$.customers", "customer id", errors);
        var parts = List(root, "parts", "$", 0, MaximumParts, errors, (item, path) =>
        {
            SessionDefinitionLoader.CheckProperties(item, ["id", "description", "unitCost"], path, errors);
            return new PlantPart(Id(item, "id", path, errors) ?? "", Text(item, "description", path, errors) ?? "",
                (decimal)Number(item, "unitCost", path, 0, 1_000_000, errors));
        }, optional: true);
        Unique(parts.Select(p => p.Id), "$.parts", "part id", errors);
        var demand = Demand(root, errors);
        var maintenance = Maintenance(root, lines, technicians, parts, errors);
        var incidents = List(root, "incidents", "$", 0, MaximumIncidents, errors, (item, path) =>
        {
            SessionDefinitionLoader.CheckProperties(item, ["failureMode", "at", "note"], path, errors);
            string mode = Text(item, "failureMode", path, errors) ?? "";
            if (maintenance is not null && maintenance.FailureModes.All(f => f.Code != mode))
                errors.Add(new("plant.unknown_failure_mode", path + ".failureMode", "Reference a failure mode code declared under maintenance.failureModes."));
            var offset = Signed(Text(item, "at", path, errors));
            if (offset is null)
                errors.Add(new("plant.invalid_incident_time", path + ".at", "Use a signed duration relative to the anchor (now), for example \"-30h\" for 30 hours ago or \"+2h\"."));
            string note = item.TryGetProperty("note", out _) ? Text(item, "note", path, errors) ?? "" : "";
            return new PlantIncident(mode, anchorUtc + (offset ?? TimeSpan.Zero), note);
        }, optional: true);
        var quality = root.TryGetProperty("quality", out _) ? Quality(root, skus, anchorUtc, errors) : null;
        var shipping = root.TryGetProperty("shipping", out _) ? Shipping(root, errors) : null;
        if (errors.ErrorCount > 0 || simulation is null) return new(null, errors.AsReadOnly());
        foreach (var (incident, index) in incidents.Select((i, n) => (i, n)))
            if (incident.AtUtc < start || (end is { } stop && incident.AtUtc >= stop))
                errors.Add(new("plant.incident_outside_timeline", $"$.incidents[{index}].at", "Place the incident inside the plant's timeline (after the backfill start and before endUtc)."));
        if (errors.ErrorCount > 0) return new(null, errors.AsReadOnly());
        return new(new PlantModel(simulation, simulation.ToLowerInvariant() + "_", tagRoot, seed, start, end, anchorUtc, sample,
            shifts, skus, lines, operators, technicians, customers, parts, demand, maintenance!, incidents) { Quality = quality, Shipping = shipping }, []);
    }

    private static (DateTimeOffset Start, DateTimeOffset? End) Timeline(JsonElement root, DateTimeOffset anchor, ValidationErrors errors)
    {
        if (!root.TryGetProperty("timeline", out var timeline) || timeline.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("plant.timeline_required", "$.timeline", "Supply timeline with backfill (for example \"7d\") or startUtc, and optionally endUtc."));
            return (anchor, null);
        }
        SessionDefinitionLoader.CheckProperties(timeline, ["backfill", "startUtc", "endUtc"], "$.timeline", errors);
        bool hasBackfill = timeline.TryGetProperty("backfill", out _), hasStart = timeline.TryGetProperty("startUtc", out _);
        DateTimeOffset start = anchor;
        if (hasBackfill == hasStart)
            errors.Add(new("plant.start_conflict", "$.timeline", "Use exactly one of backfill or startUtc."));
        else if (hasBackfill)
        {
            var span = BackfillResolver.ParseDuration(Text(timeline, "backfill", "$.timeline", errors));
            if (span is null) errors.Add(new("plant.invalid_backfill", "$.timeline.backfill", "Use a whole number followed by s, m, h or d, for example \"7d\"."));
            else start = new DateTimeOffset(anchor.UtcTicks - span.Value.Ticks - (anchor.UtcTicks - span.Value.Ticks) % TimeSpan.TicksPerMinute, TimeSpan.Zero);
        }
        else start = Utc(timeline, "startUtc", "$.timeline", errors) ?? anchor;
        DateTimeOffset? end = timeline.TryGetProperty("endUtc", out _) ? Utc(timeline, "endUtc", "$.timeline", errors) : null;
        if (end is { } e && e <= start)
            errors.Add(new("plant.invalid_time_range", "$.timeline.endUtc", "endUtc must be later than the start. Omit it to run until stopped."));
        return (start, end);
    }

    private static IReadOnlyList<PlantShift> Shifts(JsonElement root, ValidationErrors errors)
    {
        var shifts = List(root, "shifts", "$", 1, 3, errors, (item, path) =>
        {
            SessionDefinitionLoader.CheckProperties(item, ["name", "startHour", "hours"], path, errors);
            return new PlantShift(Id(item, "name", path, errors) ?? "", Integer(item, "startHour", path, 0, 23, errors), Integer(item, "hours", path, 1, 24, errors));
        });
        Unique(shifts.Select(s => s.Name), "$.shifts", "shift name", errors);
        // Shifts must tile the day exactly once so "who is on shift" always has one answer.
        var covered = new int[24];
        foreach (var shift in shifts)
            for (int h = 0; h < shift.Hours; h++) covered[(shift.StartHour + h) % 24]++;
        if (shifts.Count > 0 && covered.Any(c => c != 1))
            errors.Add(new("plant.shift_coverage", "$.shifts", "Shifts must cover all 24 UTC hours exactly once, for example Day 6–18 and Night 18–6 (startHour 6, hours 12; startHour 18, hours 12)."));
        return shifts;
    }

    private static IReadOnlyList<PlantSku> Skus(JsonElement root, ValidationErrors errors)
    {
        var skus = List(root, "skus", "$", 1, MaximumSkus, errors, (item, path) =>
        {
            SessionDefinitionLoader.CheckProperties(item, ["code", "description", "casesPerMinute", "rejectPercent", "fillTarget", "fillUnit"], path, errors);
            return new PlantSku(Id(item, "code", path, errors) ?? "", Text(item, "description", path, errors) ?? "",
                Number(item, "casesPerMinute", path, 0.1, 1000, errors), Number(item, "rejectPercent", path, 0, 20, errors),
                Number(item, "fillTarget", path, 0.001, 100000, errors), Text(item, "fillUnit", path, errors) ?? "");
        });
        Unique(skus.Select(s => s.Code), "$.skus", "SKU code", errors);
        return skus;
    }

    private static IReadOnlyList<PlantLine> Lines(JsonElement root, IReadOnlyList<PlantSku> skus, ValidationErrors errors)
    {
        var lines = List(root, "lines", "$", 1, MaximumLines, errors, (item, path) =>
        {
            SessionDefinitionLoader.CheckProperties(item, ["id", "name", "changeoverMinutes", "skus", "equipment"], path, errors);
            var lineSkus = List(item, "skus", path, 1, MaximumSkus, errors, (sku, skuPath) =>
            {
                string code = sku.ValueKind == JsonValueKind.String ? sku.GetString()! : "";
                if (skus.All(s => s.Code != code))
                    errors.Add(new("plant.unknown_sku", skuPath, "Reference a SKU code declared under skus."));
                return code;
            });
            var equipment = List(item, "equipment", path, 1, MaximumEquipmentPerLine, errors, (unit, unitPath) =>
            {
                SessionDefinitionLoader.CheckProperties(unit, ["id", "name", "class"], unitPath, errors);
                string cls = Text(unit, "class", unitPath, errors) ?? "";
                if (cls is not ("filler" or "capper" or "labeler" or "packer" or "palletizer" or "conveyor" or "mixer" or "other"))
                    errors.Add(new("plant.invalid_equipment_class", unitPath + ".class", "Use filler, capper, labeler, packer, palletizer, conveyor, mixer or other."));
                return new PlantEquipment(Id(unit, "id", unitPath, errors) ?? "", Text(unit, "name", unitPath, errors) ?? "", cls);
            });
            Unique(equipment.Select(e => e.Id), path + ".equipment", "equipment id", errors);
            return new PlantLine(Id(item, "id", path, errors) ?? "", Text(item, "name", path, errors) ?? "",
                Integer(item, "changeoverMinutes", path, 0, 480, errors), lineSkus, equipment);
        });
        Unique(lines.Select(l => l.Id), "$.lines", "line id", errors);
        return lines;
    }

    private static (IReadOnlyList<PlantPerson>, IReadOnlyList<PlantPerson>) People(JsonElement root, IReadOnlyList<PlantShift> shifts,
        IReadOnlyList<PlantLine> lines, ValidationErrors errors)
    {
        if (!root.TryGetProperty("people", out var people) || people.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("plant.people_required", "$.people", "Supply people with operators and technicians."));
            return ([], []);
        }
        SessionDefinitionLoader.CheckProperties(people, ["operators", "technicians"], "$.people", errors);
        PlantPerson Person(JsonElement item, string path, bool technician)
        {
            SessionDefinitionLoader.CheckProperties(item, technician ? ["id", "name", "shift", "skills"] : ["id", "name", "shift", "line"], path, errors);
            string shift = Text(item, "shift", path, errors) ?? "";
            if (shifts.All(s => s.Name != shift)) errors.Add(new("plant.unknown_shift", path + ".shift", "Reference a shift name declared under shifts."));
            string? line = null;
            if (!technician)
            {
                line = Text(item, "line", path, errors);
                if (line is not null && lines.All(l => l.Id != line)) errors.Add(new("plant.unknown_line", path + ".line", "Reference a line id declared under lines."));
            }
            var skills = technician
                ? List(item, "skills", path, 1, 4, errors, (skill, skillPath) => skill.ValueKind == JsonValueKind.String && Skill().IsMatch(skill.GetString()!)
                    ? skill.GetString()! : Invalid(errors, skillPath, "plant.invalid_skill", "Use a lower-case skill name such as mechanical or electrical."))
                : [];
            return new PlantPerson(Id(item, "id", path, errors) ?? "", Text(item, "name", path, errors) ?? "", shift, line, skills);
        }
        var operators = List(people, "operators", "$.people", 1, MaximumOperators, errors, (i, p) => Person(i, p, false));
        var technicians = List(people, "technicians", "$.people", 1, MaximumTechnicians, errors, (i, p) => Person(i, p, true));
        Unique(operators.Select(o => o.Id).Concat(technicians.Select(t => t.Id)), "$.people", "person id", errors);
        return (operators, technicians);
    }

    private static PlantDemand Demand(JsonElement root, ValidationErrors errors)
    {
        if (!root.TryGetProperty("demand", out var demand) || demand.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("plant.demand_required", "$.demand", "Supply demand with ordersPerDay, casesPerOrder and dueDays."));
            return new(0, new(0, 0), new(0, 0));
        }
        SessionDefinitionLoader.CheckProperties(demand, ["ordersPerDay", "casesPerOrder", "dueDays"], "$.demand", errors);
        return new(Number(demand, "ordersPerDay", "$.demand", 0.1, 50, errors),
            Range(demand, "casesPerOrder", "$.demand", 1, 1_000_000, errors), Range(demand, "dueDays", "$.demand", 0.5, 60, errors));
    }

    private static PlantMaintenance? Maintenance(JsonElement root, IReadOnlyList<PlantLine> lines, IReadOnlyList<PlantPerson> technicians,
        IReadOnlyList<PlantPart> parts, ValidationErrors errors)
    {
        const string path = "$.maintenance";
        if (!root.TryGetProperty("maintenance", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("plant.maintenance_required", path, "Supply maintenance with detectionMinutes, responseMinutes, failureModes, preventive and minorStops."));
            return null;
        }
        SessionDefinitionLoader.CheckProperties(item, ["detectionMinutes", "responseMinutes", "failureModes", "preventive", "minorStops"], path, errors);
        int detection = Integer(item, "detectionMinutes", path, 1, 60, errors);
        var skills = technicians.SelectMany(t => t.Skills).ToHashSet(StringComparer.Ordinal);
        void CheckUnit(string line, string equipment, string skill, string unitPath)
        {
            var match = lines.FirstOrDefault(l => l.Id == line);
            if (match is null) errors.Add(new("plant.unknown_line", unitPath + ".line", "Reference a line id declared under lines."));
            else if (match.Equipment.All(e => e.Id != equipment))
                errors.Add(new("plant.unknown_equipment", unitPath + ".equipment", "Reference an equipment id declared on that line."));
            if (!skills.Contains(skill))
                errors.Add(new("plant.unstaffed_skill", unitPath + ".skill", "No technician has this skill, so the work could never be done. Add the skill to a technician or change it."));
        }
        var modes = List(item, "failureModes", path, 1, MaximumFailureModes, errors, (mode, modePath) =>
        {
            SessionDefinitionLoader.CheckProperties(mode, ["code", "line", "equipment", "description", "mtbfRunHours", "repairMinutes", "skill", "priority", "warningHours", "parts"], modePath, errors);
            string line = Text(mode, "line", modePath, errors) ?? "", unit = Text(mode, "equipment", modePath, errors) ?? "", skill = Text(mode, "skill", modePath, errors) ?? "";
            CheckUnit(line, unit, skill, modePath);
            string priority = Text(mode, "priority", modePath, errors) ?? "";
            if (priority is not ("Low" or "Medium" or "High" or "Critical"))
                errors.Add(new("plant.invalid_priority", modePath + ".priority", "Use Low, Medium, High or Critical."));
            var used = List(mode, "parts", modePath, 0, 6, errors, (use, usePath) =>
            {
                SessionDefinitionLoader.CheckProperties(use, ["part", "quantity"], usePath, errors);
                string part = Text(use, "part", usePath, errors) ?? "";
                if (parts.All(p => p.Id != part)) errors.Add(new("plant.unknown_part", usePath + ".part", "Reference a part id declared under parts."));
                return new PlantPartUse(part, Integer(use, "quantity", usePath, 1, 100, errors));
            }, optional: true);
            return new PlantFailureMode(Id(mode, "code", modePath, errors) ?? "", line, unit, Text(mode, "description", modePath, errors) ?? "",
                Number(mode, "mtbfRunHours", modePath, 0.5, 100_000, errors), Range(mode, "repairMinutes", modePath, 1, 2880, errors),
                skill, priority, mode.TryGetProperty("warningHours", out _) ? Number(mode, "warningHours", modePath, 0, 240, errors) : 0, used);
        });
        var preventive = List(item, "preventive", path, 0, MaximumPreventive, errors, (pm, pmPath) =>
        {
            SessionDefinitionLoader.CheckProperties(pm, ["code", "line", "equipment", "description", "everyDays", "startHour", "durationMinutes", "skill"], pmPath, errors);
            string line = Text(pm, "line", pmPath, errors) ?? "", unit = Text(pm, "equipment", pmPath, errors) ?? "", skill = Text(pm, "skill", pmPath, errors) ?? "";
            CheckUnit(line, unit, skill, pmPath);
            return new PlantPreventive(Id(pm, "code", pmPath, errors) ?? "", line, unit, Text(pm, "description", pmPath, errors) ?? "",
                Integer(pm, "everyDays", pmPath, 1, 90, errors), Integer(pm, "startHour", pmPath, 0, 23, errors),
                Integer(pm, "durationMinutes", pmPath, 10, 1440, errors), skill);
        }, optional: true);
        Unique(modes.Select(m => m.Code).Concat(preventive.Select(p => p.Code)), path, "failure or PM code", errors);
        PlantMinorStops minor = new(0, new(1, 1));
        if (item.TryGetProperty("minorStops", out var stops) && stops.ValueKind == JsonValueKind.Object)
        {
            SessionDefinitionLoader.CheckProperties(stops, ["perRunHour", "minutes"], path + ".minorStops", errors);
            minor = new(Number(stops, "perRunHour", path + ".minorStops", 0, 30, errors), Range(stops, "minutes", path + ".minorStops", 0.1, 60, errors));
            // A minor stop the detector would see is a breakdown, not a minor stop.
            if (minor.Minutes.Maximum >= detection)
                errors.Add(new("plant.minor_stop_too_long", path + ".minorStops.minutes.maximum", "Minor stops must be shorter than detectionMinutes; longer stops are breakdowns and need a failure mode."));
        }
        else errors.Add(new("plant.minor_stops_required", path + ".minorStops", "Supply minorStops with perRunHour (0 for none) and minutes."));
        return new(detection, Range(item, "responseMinutes", path, 0, 480, errors), modes, preventive, minor);
    }

    private static PlantQuality? Quality(JsonElement root, IReadOnlyList<PlantSku> skus, DateTimeOffset anchor, ValidationErrors errors)
    {
        const string path = "$.quality";
        var item = root.GetProperty("quality");
        if (item.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("plant.object_required", path, "Supply quality as an object with parameters, labMinutes, holdMinutes and optional excursions, or leave it out."));
            return null;
        }
        SessionDefinitionLoader.CheckProperties(item, ["parameters", "labMinutes", "holdMinutes", "excursions"], path, errors);
        var parameters = List(item, "parameters", path, 1, 4, errors, (p, pPath) =>
        {
            SessionDefinitionLoader.CheckProperties(p, ["name", "unit", "target", "lower", "upper", "sigma", "skuOffsets"], pPath, errors);
            string name = Id(p, "name", pPath, errors) ?? "";
            double target = Number(p, "target", pPath, -1e6, 1e6, errors), lower = Number(p, "lower", pPath, -1e6, 1e6, errors), upper = Number(p, "upper", pPath, -1e6, 1e6, errors);
            if (!(lower < target && target < upper))
                errors.Add(new("plant.invalid_spec", pPath, "Use lower < target < upper."));
            var offsets = new Dictionary<string, double>(StringComparer.Ordinal);
            if (p.TryGetProperty("skuOffsets", out var map))
            {
                if (map.ValueKind != JsonValueKind.Object) errors.Add(new("plant.invalid_offsets", pPath + ".skuOffsets", "Use an object of SKU code to offset, for example {\"BB-BR-20\": 0.75}."));
                else foreach (var entry in map.EnumerateObject())
                {
                    if (skus.All(s => s.Code != entry.Name)) errors.Add(new("plant.unknown_sku", pPath + ".skuOffsets", "Reference SKU codes declared under skus."));
                    else if (entry.Value.ValueKind != JsonValueKind.Number || !entry.Value.TryGetDouble(out double offset) || !double.IsFinite(offset))
                        errors.Add(new("plant.invalid_number", pPath + ".skuOffsets", "Offsets must be finite numbers."));
                    else offsets[entry.Name] = offset;
                }
            }
            return new PlantQualityParameter(name, Text(p, "unit", pPath, errors) ?? "", target, lower, upper, Number(p, "sigma", pPath, 0, 1e6, errors), offsets);
        });
        Unique(parameters.Select(p => p.Name), path + ".parameters", "quality parameter name", errors);
        var excursions = List(item, "excursions", path, 0, 6, errors, (e, ePath) =>
        {
            SessionDefinitionLoader.CheckProperties(e, ["parameter", "from", "until", "shift", "sku", "note"], ePath, errors);
            string parameter = Text(e, "parameter", ePath, errors) ?? "";
            if (parameters.All(p => p.Name != parameter)) errors.Add(new("plant.unknown_parameter", ePath + ".parameter", "Reference a quality parameter name."));
            var from = Signed(Text(e, "from", ePath, errors));
            var until = Signed(Text(e, "until", ePath, errors));
            if (from is null || until is null || until <= from)
                errors.Add(new("plant.invalid_excursion_time", ePath, "Use signed durations relative to the anchor with from before until, for example \"from\": \"-10d\", \"until\": \"-6d\"."));
            string? sku = e.TryGetProperty("sku", out _) ? Text(e, "sku", ePath, errors) : null;
            if (sku is not null && skus.All(s => s.Code != sku)) errors.Add(new("plant.unknown_sku", ePath + ".sku", "Reference a SKU code declared under skus, or leave sku out to affect every SKU."));
            return new PlantExcursion(parameter, anchor + (from ?? TimeSpan.Zero), anchor + (until ?? TimeSpan.Zero),
                Number(e, "shift", ePath, -1e6, 1e6, errors), sku, e.TryGetProperty("note", out _) ? Text(e, "note", ePath, errors) ?? "" : "");
        }, optional: true);
        return new(parameters, Range(item, "labMinutes", path, 1, 1440, errors), Range(item, "holdMinutes", path, 10, 10080, errors), excursions);
    }

    private static PlantShipping? Shipping(JsonElement root, ValidationErrors errors)
    {
        const string path = "$.shipping";
        var item = root.GetProperty("shipping");
        if (item.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("plant.object_required", path, "Supply shipping as an object with delayHours and carriers, or leave it out."));
            return null;
        }
        SessionDefinitionLoader.CheckProperties(item, ["delayHours", "carriers"], path, errors);
        var carriers = List(item, "carriers", path, 1, 4, errors, (c, cPath) => c.ValueKind == JsonValueKind.String && c.GetString()!.Length is > 0 and <= 80
            ? c.GetString()! : Invalid(errors, cPath, "plant.invalid_text", "Use carrier names of 1–80 characters."));
        return new(Range(item, "delayHours", path, 0, 240, errors), carriers);
    }

    // ---- small typed readers; each reports a precise path and a correction ----

    private static List<T> List<T>(JsonElement parent, string name, string path, int minimum, int maximum, ValidationErrors errors,
        Func<JsonElement, string, T> read, bool optional = false)
    {
        var result = new List<T>();
        if (!parent.TryGetProperty(name, out var array))
        {
            if (!optional || minimum > 0) errors.Add(new("plant.required", $"{path}.{name}", FormattableString.Invariant($"Supply {name} as an array of {minimum}–{maximum} items.")));
            return result;
        }
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() < minimum || array.GetArrayLength() > maximum)
        {
            errors.Add(new("plant.invalid_count", $"{path}.{name}", FormattableString.Invariant($"Supply {name} as an array of {minimum}–{maximum} items. Demo plants are deliberately small.")));
            return result;
        }
        int index = 0;
        foreach (var item in array.EnumerateArray())
        {
            string itemPath = $"{path}.{name}[{index++}]";
            if (item.ValueKind != JsonValueKind.Object && item.ValueKind != JsonValueKind.String)
            {
                errors.Add(new("plant.object_required", itemPath, "Expected an object."));
                continue;
            }
            result.Add(read(item, itemPath));
        }
        return result;
    }

    private static string? Text(JsonElement item, string name, string path, ValidationErrors errors)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        string? value = SessionDefinitionLoader.ReadString(item, name, path, errors);
        if (value is not null && (value.Length == 0 || value.Length > 200))
        {
            errors.Add(new("plant.invalid_text", $"{path}.{name}", "Use 1–200 characters."));
            return null;
        }
        return value;
    }

    private static string? Id(JsonElement item, string name, string path, ValidationErrors errors)
    {
        string? value = Text(item, name, path, errors);
        if (value is not null && !Identifier().IsMatch(value))
        {
            errors.Add(new("plant.invalid_id", $"{path}.{name}", "Use 1–32 letters, digits or hyphens, starting with a letter. Ids become tag path segments and record keys."));
            return null;
        }
        return value;
    }

    private static int Integer(JsonElement item, string name, string path, int minimum, int maximum, ValidationErrors errors)
    {
        if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number >= minimum && number <= maximum)
            return number;
        errors.Add(new("plant.invalid_integer", $"{path}.{name}", FormattableString.Invariant($"Supply an integer from {minimum} through {maximum}.")));
        return minimum;
    }

    private static double Number(JsonElement item, string name, string path, double minimum, double maximum, ValidationErrors errors)
    {
        if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) &&
            double.IsFinite(number) && number >= minimum && number <= maximum)
            return number;
        errors.Add(new("plant.invalid_number", $"{path}.{name}", FormattableString.Invariant($"Supply a number from {minimum} through {maximum}.")));
        return minimum;
    }

    private static PlantRange Range(JsonElement item, string name, string path, double minimum, double maximum, ValidationErrors errors)
    {
        if (!item.TryGetProperty(name, out var range) || range.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("plant.range_required", $"{path}.{name}", "Supply an object with minimum and maximum."));
            return new(minimum, minimum);
        }
        SessionDefinitionLoader.CheckProperties(range, ["minimum", "maximum"], $"{path}.{name}", errors);
        double low = Number(range, "minimum", $"{path}.{name}", minimum, maximum, errors), high = Number(range, "maximum", $"{path}.{name}", minimum, maximum, errors);
        if (low > high) errors.Add(new("plant.invalid_range", $"{path}.{name}", "maximum must be at least minimum."));
        return new(low, high);
    }

    private static DateTimeOffset? Utc(JsonElement item, string name, string path, ValidationErrors errors)
    {
        string? text = Text(item, name, path, errors);
        if (text is not null && text.EndsWith('Z') && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
            return value.ToUniversalTime();
        if (text is not null) errors.Add(new("plant.invalid_utc", $"{path}.{name}", "Use an ISO 8601 UTC time ending in Z, for example 2026-11-20T00:00:00Z."));
        return null;
    }

    private static TimeSpan? Signed(string? text)
    {
        if (text is null || text.Length < 3 || text[0] is not ('-' or '+')) return null;
        var span = BackfillResolver.ParseDuration(text[1..]);
        return span is null ? null : text[0] == '-' ? -span.Value : span.Value;
    }

    private static void Unique(IEnumerable<string> values, string path, string what, ValidationErrors errors)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
            if (value.Length > 0 && !seen.Add(value))
                errors.Add(new("plant.duplicate_id", path, $"Each {what} must be unique (case-insensitive)."));
    }

    private static string Invalid(ValidationErrors errors, string path, string code, string message)
    {
        errors.Add(new(code, path, message));
        return "";
    }

    [GeneratedRegex("^[A-Z][A-Za-z0-9]{1,31}$", RegexOptions.CultureInvariant)] private static partial Regex SimulationName();
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9-]{0,31}$", RegexOptions.CultureInvariant)] private static partial Regex Identifier();
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)] private static partial Regex TagRoot();
    [GeneratedRegex("^[a-z][a-z-]{1,31}$", RegexOptions.CultureInvariant)] private static partial Regex Skill();
}
