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
            "tagRoot", "shifts", "skus", "lines", "people", "customers", "parts", "demand", "maintenance", "incidents", "quality", "shipping", "drifts"], "$", errors);
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
        var (operators, technicians, qualityStaff) = People(root, shifts, lines, errors);
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
            SessionDefinitionLoader.CheckProperties(item, ["failureMode", "at", "note", "shift"], path, errors);
            string mode = Text(item, "failureMode", path, errors) ?? "";
            if (maintenance is not null && maintenance.FailureModes.All(f => f.Code != mode))
                errors.Add(new("plant.unknown_failure_mode", path + ".failureMode", "Reference a failure mode code declared under maintenance.failureModes."));
            var offset = Signed(Text(item, "at", path, errors));
            if (offset is null)
                errors.Add(new("plant.invalid_incident_time", path + ".at", "Use a signed duration relative to the anchor (now), for example \"-30h\" for 30 hours ago or \"+2h\"."));
            string note = item.TryGetProperty("note", out _) ? Text(item, "note", path, errors) ?? "" : "";
            var at = anchorUtc + (offset ?? TimeSpan.Zero);
            string? shift = null;
            if (item.TryGetProperty("shift", out _))
            {
                shift = Text(item, "shift", path, errors);
                var match = shifts.FirstOrDefault(s => s.Name == shift);
                if (match is null) errors.Add(new("plant.unknown_shift", path + ".shift", "Reference a shift name declared under shifts, or leave shift out."));
                else at = IntoShift(at, match);
            }
            return new PlantIncident(mode, at, note) { Shift = shift };
        }, optional: true);
        if (maintenance is not null && root.TryGetProperty("maintenance", out var maintenanceItem) && maintenanceItem.ValueKind == JsonValueKind.Object &&
            maintenanceItem.TryGetProperty("certification", out _))
            maintenance = maintenance with { Certification = Certification(maintenanceItem, maintenance, lines, operators, technicians, anchorUtc, errors) };
        var quality = root.TryGetProperty("quality", out _) ? Quality(root, skus, lines, qualityStaff, anchorUtc, errors) : null;
        var shipping = root.TryGetProperty("shipping", out _) ? Shipping(root, errors) : null;
        var drifts = root.TryGetProperty("drifts", out _) ? Drifts(root, lines, maintenance, anchorUtc, errors) : [];
        if (qualityStaff.Count > 0 && quality?.Deviations is null)
            errors.Add(new("plant.unused_qa", "$.people.qa", "QA staff only close batch deviations. Add quality.deviations, or remove people.qa."));
        if (errors.ErrorCount > 0 || simulation is null) return new(null, errors.AsReadOnly());
        foreach (var (incident, index) in incidents.Select((i, n) => (i, n)))
            if (incident.AtUtc < start || (end is { } stop && incident.AtUtc >= stop))
                errors.Add(new("plant.incident_outside_timeline", $"$.incidents[{index}].at", "Place the incident inside the plant's timeline (after the backfill start and before endUtc)."));
        if (errors.ErrorCount > 0) return new(null, errors.AsReadOnly());
        return new(new PlantModel(simulation, simulation.ToLowerInvariant() + "_", tagRoot, seed, start, end, anchorUtc, sample,
            shifts, skus, lines, operators, technicians, customers, parts, demand, maintenance!, incidents)
        { Quality = quality, Shipping = shipping, Drifts = drifts, QualityStaff = qualityStaff }, []);
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
            SessionDefinitionLoader.CheckProperties(item, ["id", "name", "kind", "changeoverMinutes", "skus", "equipment", "batch"], path, errors);
            string kind = item.TryGetProperty("kind", out _) ? Text(item, "kind", path, errors) ?? "packaging" : "packaging";
            if (kind is not ("packaging" or "batch"))
                errors.Add(new("plant.invalid_line_kind", path + ".kind", "Use \"packaging\" (a continuous line, the default) or \"batch\" (a batch unit that runs recipes)."));
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
                if (cls is not ("filler" or "capper" or "labeler" or "packer" or "palletizer" or "conveyor" or "mixer" or "reactor" or "agitator" or "pump" or "jacket" or "valve" or "other"))
                    errors.Add(new("plant.invalid_equipment_class", unitPath + ".class", "Use filler, capper, labeler, packer, palletizer, conveyor, mixer, reactor, agitator, pump, jacket, valve or other."));
                return new PlantEquipment(Id(unit, "id", unitPath, errors) ?? "", Text(unit, "name", unitPath, errors) ?? "", cls);
            });
            Unique(equipment.Select(e => e.Id), path + ".equipment", "equipment id", errors);
            PlantBatchUnit? batch = null;
            if (kind == "batch") batch = BatchUnit(item, path, lineSkus, errors);
            else if (item.TryGetProperty("batch", out _))
                errors.Add(new("plant.batch_on_packaging_line", path + ".batch", "Only a line with \"kind\": \"batch\" has a batch section. Set kind to \"batch\" or remove batch."));
            return new PlantLine(Id(item, "id", path, errors) ?? "", Text(item, "name", path, errors) ?? "",
                Integer(item, "changeoverMinutes", path, 0, 480, errors), lineSkus, equipment) { Kind = kind, Batch = batch };
        });
        Unique(lines.Select(l => l.Id), "$.lines", "line id", errors);
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Batch is not { } batch) continue;
            string path = $"$.lines[{i}]";
            // A batch SKU is in kg or L, a packaging SKU in cases: one SKU cannot be both.
            foreach (var sku in lines[i].Skus)
                if (lines.Any(l => l.Batch is null && l.Skus.Contains(sku)))
                    errors.Add(new("plant.batch_sku_shared", path + ".skus", $"SKU {sku} is made on this batch unit and on a packaging line. Give the batch product its own SKU, and use batch.feeds to let the packaging line consume it."));
            if (batch.Feeds is not { } feeds) continue;
            string feedsPath = path + ".batch.feeds";
            var fed = lines.FirstOrDefault(l => l.Id == feeds.Line);
            if (fed is null || fed.Batch is not null)
                errors.Add(new("plant.invalid_feeds", feedsPath + ".line", "Reference the id of a packaging line declared under lines."));
            for (int c = 0; c < feeds.Consumption.Count; c++)
            {
                var use = feeds.Consumption[c];
                string usePath = $"{feedsPath}.consumption[{c}]";
                if (fed is not null && !fed.Skus.Contains(use.Sku))
                    errors.Add(new("plant.invalid_feeds", usePath + ".sku", $"Reference a SKU the fed line {feeds.Line} makes."));
                if (!lines[i].Skus.Contains(use.Intermediate))
                    errors.Add(new("plant.invalid_feeds", usePath + ".intermediate", "Reference a SKU this batch unit makes (listed under the line's skus)."));
            }
            Unique(feeds.Consumption.Select(c => c.Sku), feedsPath + ".consumption", "consuming SKU", errors);
        }
        if (lines.Count(l => l.Batch?.Feeds is not null) > 0)
        {
            var consumers = lines.Where(l => l.Batch?.Feeds is not null).SelectMany(l => l.Batch!.Feeds!.Consumption.Select(c => c.Sku)).ToList();
            if (consumers.Count != consumers.Distinct(StringComparer.Ordinal).Count())
                errors.Add(new("plant.invalid_feeds", "$.lines", "Each packaging SKU can consume the intermediate of one batch unit only."));
        }
        return lines;
    }

    private static PlantBatchUnit? BatchUnit(JsonElement line, string linePath, IReadOnlyList<string> lineSkus, ValidationErrors errors)
    {
        string path = linePath + ".batch";
        if (!line.TryGetProperty("batch", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("plant.batch_required", path, "A batch line needs batch with capacity, unit and recipes (and optionally feeds)."));
            return null;
        }
        SessionDefinitionLoader.CheckProperties(item, ["capacity", "unit", "recipes", "feeds", "goldenThreshold"], path, errors);
        double capacity = Number(item, "capacity", path, 1, 1_000_000, errors);
        string unit = Text(item, "unit", path, errors) ?? "";
        PlantRamp Ramp(JsonElement parent, string name, string parentPath, double minimum, double maximum, string example)
        {
            string rampPath = $"{parentPath}.{name}";
            if (!parent.TryGetProperty(name, out var ramp) || ramp.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new("plant.ramp_required", rampPath, $"Supply {name} as an object with start and end, for example {example}."));
                return new(minimum, minimum);
            }
            SessionDefinitionLoader.CheckProperties(ramp, ["start", "end"], rampPath, errors);
            return new(Number(ramp, "start", rampPath, minimum, maximum, errors), Number(ramp, "end", rampPath, minimum, maximum, errors));
        }
        var recipes = List(item, "recipes", path, 1, MaximumSkus, errors, (recipe, recipePath) =>
        {
            SessionDefinitionLoader.CheckProperties(recipe, ["id", "sku", "phases", "golden"], recipePath, errors);
            string sku = Text(recipe, "sku", recipePath, errors) ?? "";
            if (!lineSkus.Contains(sku))
                errors.Add(new("plant.recipe_sku", recipePath + ".sku", "Reference a SKU made on this batch line (listed under the line's skus)."));
            var phases = List(recipe, "phases", recipePath, 1, 12, errors, (phase, phasePath) =>
            {
                SessionDefinitionLoader.CheckProperties(phase, ["name", "minutes", "setpoints", "levelPercent"], phasePath, errors);
                string name = Id(phase, "name", phasePath, errors) ?? "";
                var minutes = Range(phase, "minutes", phasePath, 1, 1440, errors);
                PlantRamp temperature = new(20, 20), pressure = new(1, 1), agitator = new(0, 0);
                if (!phase.TryGetProperty("setpoints", out var setpoints) || setpoints.ValueKind != JsonValueKind.Object)
                    errors.Add(new("plant.setpoints_required", phasePath + ".setpoints", "Supply setpoints with temperatureC, pressureBar and agitatorRpm, each as {\"start\": …, \"end\": …}."));
                else
                {
                    string setpointsPath = phasePath + ".setpoints";
                    SessionDefinitionLoader.CheckProperties(setpoints, ["temperatureC", "pressureBar", "agitatorRpm"], setpointsPath, errors);
                    temperature = Ramp(setpoints, "temperatureC", setpointsPath, -20, 300, "{\"start\": 25, \"end\": 80}");
                    pressure = Ramp(setpoints, "pressureBar", setpointsPath, 0, 20, "{\"start\": 1.0, \"end\": 2.5}");
                    agitator = Ramp(setpoints, "agitatorRpm", setpointsPath, 0, 500, "{\"start\": 60, \"end\": 120}");
                }
                var level = Ramp(phase, "levelPercent", phasePath, 0, 100, "{\"start\": 0, \"end\": 65}");
                return new PlantPhase(name, minutes, temperature, pressure, agitator, level);
            });
            var parsed = new PlantRecipe(Id(recipe, "id", recipePath, errors) ?? "", sku, phases);
            return recipe.TryGetProperty("golden", out _) ? parsed with { Golden = Golden(recipe, recipePath, phases, errors) } : parsed;
        });
        Unique(recipes.Select(r => r.Id), path + ".recipes", "recipe id", errors);
        foreach (var sku in lineSkus)
            if (recipes.Count(r => r.Sku == sku) != 1)
                errors.Add(new("plant.recipe_sku", path + ".recipes", $"Give SKU {sku} exactly one recipe on this batch line."));
        PlantFeeds? feeds = null;
        if (item.TryGetProperty("feeds", out var feedsItem))
        {
            string feedsPath = path + ".feeds";
            if (feedsItem.ValueKind != JsonValueKind.Object)
                errors.Add(new("plant.object_required", feedsPath, "Supply feeds as an object with line and consumption, or leave it out."));
            else
            {
                SessionDefinitionLoader.CheckProperties(feedsItem, ["line", "consumption"], feedsPath, errors);
                var consumption = List(feedsItem, "consumption", feedsPath, 1, MaximumSkus, errors, (use, usePath) =>
                {
                    SessionDefinitionLoader.CheckProperties(use, ["sku", "intermediate", "perCase"], usePath, errors);
                    return new PlantConsumption(Text(use, "sku", usePath, errors) ?? "", Text(use, "intermediate", usePath, errors) ?? "",
                        Number(use, "perCase", usePath, 0.001, 100_000, errors));
                });
                feeds = new PlantFeeds(Text(feedsItem, "line", feedsPath, errors) ?? "", consumption);
            }
        }
        double threshold = item.TryGetProperty("goldenThreshold", out _) ? Number(item, "goldenThreshold", path, 0.1, 10, errors) : 1.0;
        return new PlantBatchUnit(capacity, unit, recipes, feeds) { GoldenThreshold = threshold };
    }

    /// <summary>A recipe's golden profile: optional per-phase golden minutes (by phase name) and the yield target and sigma.</summary>
    private static PlantGolden Golden(JsonElement recipe, string recipePath, IReadOnlyList<PlantPhase> phases, ValidationErrors errors)
    {
        string path = recipePath + ".golden";
        var defaults = PlantGolden.Default(phases);
        var item = recipe.GetProperty("golden");
        if (item.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("plant.object_required", path, "Supply golden as an object with optional phaseMinutes and yieldPercent, or leave it out (the middle of each phase's range and a 95 % yield)."));
            return defaults;
        }
        SessionDefinitionLoader.CheckProperties(item, ["phaseMinutes", "yieldPercent"], path, errors);
        var minutes = defaults.PhaseMinutes.ToList();
        if (item.TryGetProperty("phaseMinutes", out var map))
        {
            if (map.ValueKind != JsonValueKind.Object)
                errors.Add(new("plant.object_required", path + ".phaseMinutes", "Use an object of phase name to golden running minutes, for example {\"Heat\": 75}."));
            else foreach (var entry in map.EnumerateObject())
            {
                int index = phases.ToList().FindIndex(p => p.Name == entry.Name);
                string entryPath = $"{path}.phaseMinutes.{entry.Name}";
                if (index < 0) errors.Add(new("plant.unknown_phase", entryPath, $"Reference a phase of this recipe: {string.Join(", ", phases.Select(p => p.Name))}."));
                else if (entry.Value.ValueKind != JsonValueKind.Number || !entry.Value.TryGetDouble(out double value) || value < phases[index].Minutes.Minimum || value > phases[index].Minutes.Maximum)
                    errors.Add(new("plant.invalid_golden", entryPath, FormattableString.Invariant($"Use a number of minutes inside the phase's range, {phases[index].Minutes.Minimum} through {phases[index].Minutes.Maximum}.")));
                else minutes[index] = value;
            }
        }
        double target = defaults.YieldTarget, sigma = defaults.YieldSigma;
        if (item.TryGetProperty("yieldPercent", out var yield))
        {
            string yieldPath = path + ".yieldPercent";
            if (yield.ValueKind != JsonValueKind.Object)
                errors.Add(new("plant.object_required", yieldPath, "Use an object with target and sigma, for example {\"target\": 96, \"sigma\": 0.6}."));
            else
            {
                SessionDefinitionLoader.CheckProperties(yield, ["target", "sigma"], yieldPath, errors);
                target = Number(yield, "target", yieldPath, 1, 100, errors);
                sigma = Number(yield, "sigma", yieldPath, 0, 20, errors);
            }
        }
        return new PlantGolden(minutes, target, sigma);
    }

    /// <summary>
    /// Planted golden-batch drifts. Each names a batch line, optionally a recipe,
    /// and a phase; it starts at from (relative to the anchor) and ends at until or
    /// when the preventive maintenance endsWith completes, whichever is first.
    /// </summary>
    private static IReadOnlyList<PlantDrift> Drifts(JsonElement root, IReadOnlyList<PlantLine> lines, PlantMaintenance? maintenance, DateTimeOffset anchor, ValidationErrors errors) =>
        List(root, "drifts", "$", 0, 6, errors, (item, path) =>
        {
            SessionDefinitionLoader.CheckProperties(item, ["line", "recipe", "phase", "cause", "from", "until", "rampDays", "endsWith", "effect"], path, errors);
            string lineId = Text(item, "line", path, errors) ?? "";
            var line = lines.FirstOrDefault(l => l.Id == lineId);
            if (line?.Batch is null)
                errors.Add(new("plant.invalid_drift", path + ".line", "Reference the id of a batch line (kind \"batch\"). Drifts change batch phases."));
            string? recipe = item.TryGetProperty("recipe", out _) ? Text(item, "recipe", path, errors) : null;
            var recipes = line?.Batch?.Recipes.Where(r => recipe is null || r.Id == recipe).ToList() ?? [];
            if (recipe is not null && line?.Batch is not null && recipes.Count == 0)
                errors.Add(new("plant.invalid_drift", path + ".recipe", "Reference a recipe id on that batch line, or leave recipe out to affect every recipe with this phase."));
            string phase = Text(item, "phase", path, errors) ?? "";
            if (line?.Batch is not null && recipes.Count > 0 && recipes.All(r => r.Phases.All(p => p.Name != phase)))
                errors.Add(new("plant.invalid_drift", path + ".phase", $"Reference a phase of the drifted recipes: {string.Join(", ", recipes.SelectMany(r => r.Phases.Select(p => p.Name)).Distinct())}."));
            string cause = Text(item, "cause", path, errors) ?? "";
            var from = Signed(Text(item, "from", path, errors));
            if (from is null) errors.Add(new("plant.invalid_drift_time", path + ".from", "Use a signed duration relative to the anchor (now), for example \"-9d\"."));
            TimeSpan? until = null;
            if (item.TryGetProperty("until", out _))
            {
                until = Signed(Text(item, "until", path, errors));
                if (until is null || (from is not null && until <= from))
                    errors.Add(new("plant.invalid_drift_time", path + ".until", "Use a signed duration relative to the anchor that is later than from, for example \"-3d\"."));
            }
            string? endsWith = item.TryGetProperty("endsWith", out _) ? Text(item, "endsWith", path, errors) : null;
            if (endsWith is not null && maintenance is not null && maintenance.Preventive.All(p => p.Code != endsWith || p.Line != lineId))
                errors.Add(new("plant.invalid_drift", path + ".endsWith", "Reference the code of a preventive maintenance task on the same line (for example a jacket cleaning). Its next completion after from ends the drift."));
            if (!item.TryGetProperty("until", out _) && endsWith is null)
                errors.Add(new("plant.invalid_drift_time", path, "Give the drift an end: until (a signed duration), endsWith (a preventive maintenance code), or both."));
            double ramp = item.TryGetProperty("rampDays", out _) ? Number(item, "rampDays", path, 0, 60, errors) : 0;
            double factor = 1, lag = 0, yieldDelta = 0;
            if (!item.TryGetProperty("effect", out var effect) || effect.ValueKind != JsonValueKind.Object)
                errors.Add(new("plant.invalid_drift", path + ".effect", "Supply effect with at least one of phaseMinutesFactor (for example 1.35), temperatureLagC or yieldDelta (for example -4)."));
            else
            {
                string effectPath = path + ".effect";
                SessionDefinitionLoader.CheckProperties(effect, ["phaseMinutesFactor", "temperatureLagC", "yieldDelta"], effectPath, errors);
                if (effect.TryGetProperty("phaseMinutesFactor", out _)) factor = Number(effect, "phaseMinutesFactor", effectPath, 0.5, 3, errors);
                if (effect.TryGetProperty("temperatureLagC", out _)) lag = Number(effect, "temperatureLagC", effectPath, 0, 50, errors);
                if (effect.TryGetProperty("yieldDelta", out _)) yieldDelta = Number(effect, "yieldDelta", effectPath, -50, 50, errors);
                if (factor == 1 && lag == 0 && yieldDelta == 0)
                    errors.Add(new("plant.invalid_drift", effectPath, "The effect changes nothing. Set phaseMinutesFactor (not 1), temperatureLagC or yieldDelta."));
            }
            return new PlantDrift(lineId, recipe, phase, cause, anchor + (from ?? TimeSpan.Zero), until is null ? null : anchor + until.Value, ramp, endsWith, factor, lag, yieldDelta);
        }, optional: true);

    private static (IReadOnlyList<PlantPerson>, IReadOnlyList<PlantPerson>, IReadOnlyList<PlantPerson>) People(JsonElement root, IReadOnlyList<PlantShift> shifts,
        IReadOnlyList<PlantLine> lines, ValidationErrors errors)
    {
        if (!root.TryGetProperty("people", out var people) || people.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("plant.people_required", "$.people", "Supply people with operators and technicians."));
            return ([], [], []);
        }
        SessionDefinitionLoader.CheckProperties(people, ["operators", "technicians", "qa"], "$.people", errors);
        PlantPerson Person(JsonElement item, string path, bool technician, bool qa = false)
        {
            SessionDefinitionLoader.CheckProperties(item, technician ? ["id", "name", "shift", "skills"] : qa ? ["id", "name", "shift"] : ["id", "name", "shift", "line"], path, errors);
            string shift = Text(item, "shift", path, errors) ?? "";
            if (shifts.All(s => s.Name != shift)) errors.Add(new("plant.unknown_shift", path + ".shift", "Reference a shift name declared under shifts."));
            string? line = null;
            if (!technician && !qa)
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
        var qaStaff = List(people, "qa", "$.people", 0, 3, errors, (i, p) => Person(i, p, false, qa: true), optional: true);
        Unique(operators.Select(o => o.Id).Concat(technicians.Select(t => t.Id)).Concat(qaStaff.Select(q => q.Id)), "$.people", "person id", errors);
        return (operators, technicians, qaStaff);
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
        SessionDefinitionLoader.CheckProperties(item, ["detectionMinutes", "responseMinutes", "failureModes", "preventive", "minorStops", "certification"], path, errors);
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

    /// <summary>
    /// Moves a planted incident into its shift: kept when it already falls at
    /// least two hours before the shift ends, otherwise moved to two hours after
    /// the shift's next start. So a story that needs a particular crew (for
    /// example the night technician) gets it whatever the anchor's time of day.
    /// </summary>
    private static DateTimeOffset IntoShift(DateTimeOffset at, PlantShift shift)
    {
        var day = new DateTimeOffset(at.UtcDateTime.Date, TimeSpan.Zero);
        for (int d = -1; d <= 1; d++)
        {
            var start = day.AddDays(d).AddHours(shift.StartHour);
            if (at >= start && at <= start.AddHours(shift.Hours - 2)) return at;
        }
        var next = day.AddHours(shift.StartHour);
        while (next <= at) next = next.AddDays(1);
        return next.AddHours(Math.Min(2, shift.Hours / 2.0));
    }

    private static PlantCertification? Certification(JsonElement maintenance, PlantMaintenance parsed, IReadOnlyList<PlantLine> lines,
        IReadOnlyList<PlantPerson> operators, IReadOnlyList<PlantPerson> technicians, DateTimeOffset anchor, ValidationErrors errors)
    {
        const string path = "$.maintenance.certification";
        var item = maintenance.GetProperty("certification");
        if (item.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("plant.object_required", path, "Supply certification as an object with validDays, issued, safetyCritical and whenNoneCertified (and optionally renewals and operators), or leave it out."));
            return null;
        }
        SessionDefinitionLoader.CheckProperties(item, ["validDays", "issued", "safetyCritical", "whenNoneCertified", "renewals", "operators"], path, errors);
        // Certificate times are whole minutes from the anchor's minute, so every record time is tidy.
        var anchorMinute = new DateTimeOffset(anchor.UtcTicks - anchor.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
        DateTimeOffset When(JsonElement entry, string entryPath)
        {
            var offset = Signed(Text(entry, "at", entryPath, errors));
            if (offset is null || offset.Value.Ticks % TimeSpan.TicksPerMinute != 0)
            {
                errors.Add(new("plant.certification_time", entryPath + ".at", "Use a signed whole-minute duration relative to the anchor (now), for example \"-300d\" for 300 days ago or \"+90m\"."));
                return anchorMinute;
            }
            return anchorMinute + offset.Value;
        }
        PlantCertificateIssue TechnicianIssue(JsonElement entry, string entryPath)
        {
            SessionDefinitionLoader.CheckProperties(entry, ["technician", "skill", "at"], entryPath, errors);
            string technician = Text(entry, "technician", entryPath, errors) ?? "", skill = Text(entry, "skill", entryPath, errors) ?? "";
            var person = technicians.FirstOrDefault(t => t.Id == technician);
            if (person is null)
                errors.Add(new("plant.unknown_certification_target", entryPath + ".technician", "Reference a technician id declared under people.technicians."));
            else if (!person.Skills.Contains(skill))
                errors.Add(new("plant.unknown_certification_target", entryPath + ".skill", $"Technician {technician} does not have this skill. Certify one of the technician's skills: {string.Join(", ", person.Skills)}."));
            return new PlantCertificateIssue(technician, skill, When(entry, entryPath));
        }
        int validDays = item.TryGetProperty("validDays", out _) ? Integer(item, "validDays", path, 1, 3650, errors) : 365;
        var issued = List(item, "issued", path, 0, 16, errors, TechnicianIssue, optional: true);
        var renewals = List(item, "renewals", path, 0, 16, errors, TechnicianIssue, optional: true);
        var codes = parsed.FailureModes.Select(m => m.Code).Concat(parsed.Preventive.Select(p => p.Code)).ToHashSet(StringComparer.Ordinal);
        var safetyCritical = List(item, "safetyCritical", path, 0, MaximumFailureModes + MaximumPreventive, errors, (code, codePath) =>
        {
            string value = code.ValueKind == JsonValueKind.String ? code.GetString()! : "";
            if (!codes.Contains(value))
                errors.Add(new("plant.unknown_certification_target", codePath, "Reference a failure mode or preventive maintenance code declared under maintenance."));
            return value;
        }, optional: true);
        string policy = item.TryGetProperty("whenNoneCertified", out _) ? Text(item, "whenNoneCertified", path, errors) ?? "" : "wait";
        if (policy is not ("wait" or "assignAnyway"))
            errors.Add(new("plant.invalid_certification_policy", path + ".whenNoneCertified", "Use \"wait\" (the ticket waits for a certified technician) or \"assignAnyway\" (a skilled but uncertified technician takes it)."));
        else if (policy == "assignAnyway" && safetyCritical.Count == 0)
            errors.Add(new("plant.invalid_certification_policy", path + ".safetyCritical", "assignAnyway only applies to safety-critical work. List at least one failure or PM code under safetyCritical."));
        PlantOperatorCertification? operatorSide = null;
        if (item.TryGetProperty("operators", out var ops))
        {
            const string opsPath = path + ".operators";
            if (ops.ValueKind != JsonValueKind.Object)
                errors.Add(new("plant.object_required", opsPath, "Supply operators as an object with workInstructions and whenOperatorUncertified, or leave it out."));
            else
            {
                SessionDefinitionLoader.CheckProperties(ops, ["workInstructions", "whenOperatorUncertified", "signoffMinutes", "rejectPercentWhenUncertified"], opsPath, errors);
                var instructions = List(ops, "workInstructions", opsPath, 1, 12, errors, (wi, wiPath) =>
                {
                    SessionDefinitionLoader.CheckProperties(wi, ["id", "line", "skus", "validDays", "issued"], wiPath, errors);
                    string line = Text(wi, "line", wiPath, errors) ?? "";
                    var match = lines.FirstOrDefault(l => l.Id == line);
                    if (match is null) errors.Add(new("plant.unknown_certification_target", wiPath + ".line", "Reference a line id declared under lines."));
                    var covered = List(wi, "skus", wiPath, 1, MaximumSkus, errors, (sku, skuPath) =>
                    {
                        string code = sku.ValueKind == JsonValueKind.String ? sku.GetString()! : "";
                        if (match is not null && !match.Skus.Contains(code))
                            errors.Add(new("plant.unknown_certification_target", skuPath, "Reference a SKU this line makes (listed under the line's skus)."));
                        return code;
                    });
                    string id = Id(wi, "id", wiPath, errors) ?? "";
                    var people = List(wi, "issued", wiPath, 0, MaximumOperators * 2, errors, (entry, entryPath) =>
                    {
                        SessionDefinitionLoader.CheckProperties(entry, ["operator", "at"], entryPath, errors);
                        string person = Text(entry, "operator", entryPath, errors) ?? "";
                        if (operators.All(o => o.Id != person))
                            errors.Add(new("plant.unknown_certification_target", entryPath + ".operator", "Reference an operator id declared under people.operators."));
                        return new PlantCertificateIssue(person, id, When(entry, entryPath));
                    }, optional: true);
                    return new PlantWorkInstruction(id, line, covered, Integer(wi, "validDays", wiPath, 1, 3650, errors), people);
                });
                Unique(instructions.Select(w => w.Id), opsPath + ".workInstructions", "work instruction id", errors);
                for (int a = 0; a < instructions.Count; a++)
                    for (int b = a + 1; b < instructions.Count; b++)
                        if (instructions[a].Line == instructions[b].Line && instructions[a].Skus.Intersect(instructions[b].Skus).Any())
                            errors.Add(new("plant.duplicate_id", $"{opsPath}.workInstructions[{b}]", "Two work instructions cover the same line and SKU. Give each line and SKU one work instruction."));
                string opsPolicy = Text(ops, "whenOperatorUncertified", opsPath, errors) ?? "";
                if (opsPolicy is not ("runAnyway" or "supervisorSignoff"))
                    errors.Add(new("plant.invalid_certification_policy", opsPath + ".whenOperatorUncertified", "Use \"runAnyway\" (the run starts anyway) or \"supervisorSignoff\" (a supervisor signs off and the run starts signoffMinutes later)."));
                int signoff = opsPolicy == "supervisorSignoff" || ops.TryGetProperty("signoffMinutes", out _) ? Integer(ops, "signoffMinutes", opsPath, 1, 480, errors) : 0;
                double? reject = ops.TryGetProperty("rejectPercentWhenUncertified", out _) ? Number(ops, "rejectPercentWhenUncertified", opsPath, 0, 20, errors) : null;
                operatorSide = new PlantOperatorCertification(instructions, opsPolicy, signoff, reject);
            }
        }
        return new PlantCertification(validDays, issued, renewals, safetyCritical, policy) { Operators = operatorSide };
    }

    private static PlantQuality? Quality(JsonElement root, IReadOnlyList<PlantSku> skus, IReadOnlyList<PlantLine> lines, IReadOnlyList<PlantPerson> qualityStaff,
        DateTimeOffset anchor, ValidationErrors errors)
    {
        const string path = "$.quality";
        var item = root.GetProperty("quality");
        if (item.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("plant.object_required", path, "Supply quality as an object with parameters, labMinutes, holdMinutes and optional excursions, or leave it out."));
            return null;
        }
        SessionDefinitionLoader.CheckProperties(item, ["parameters", "labMinutes", "holdMinutes", "excursions", "batchRelease", "deviations"], path, errors);
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
        PlantBatchRelease? release = null;
        if (item.TryGetProperty("batchRelease", out var releaseItem))
        {
            const string releasePath = path + ".batchRelease";
            if (lines.All(l => l.Batch is null))
                errors.Add(new("plant.no_batch_line", releasePath, "batchRelease tests batches, and this plant has no batch line. Remove it, or add a line with kind \"batch\"."));
            if (releaseItem.ValueKind != JsonValueKind.Object)
                errors.Add(new("plant.object_required", releasePath, "Supply batchRelease as an object with labMinutes and parameters, or leave it out."));
            else
            {
                SessionDefinitionLoader.CheckProperties(releaseItem, ["labMinutes", "parameters"], releasePath, errors);
                var tests = List(releaseItem, "parameters", releasePath, 1, 4, errors, (t, tPath) =>
                {
                    SessionDefinitionLoader.CheckProperties(t, ["name", "lower", "upper", "yieldSensitivity"], tPath, errors);
                    string name = Text(t, "name", tPath, errors) ?? "";
                    var parameter = parameters.FirstOrDefault(p => p.Name == name);
                    if (parameter is null) errors.Add(new("plant.unknown_parameter", tPath + ".name", "Reference a quality parameter name declared under quality.parameters."));
                    double? lower = t.TryGetProperty("lower", out _) ? Number(t, "lower", tPath, -1e6, 1e6, errors) : null;
                    double? upper = t.TryGetProperty("upper", out _) ? Number(t, "upper", tPath, -1e6, 1e6, errors) : null;
                    if (parameter is not null && !((lower ?? parameter.Lower) < parameter.Target && parameter.Target < (upper ?? parameter.Upper)))
                        errors.Add(new("plant.invalid_spec", tPath, "Release limits must keep lower < target < upper."));
                    double sensitivity = t.TryGetProperty("yieldSensitivity", out _) ? Number(t, "yieldSensitivity", tPath, -1e3, 1e3, errors) : 0;
                    return new PlantBatchTest(name, lower, upper, sensitivity);
                });
                Unique(tests.Select(t => t.Parameter), releasePath + ".parameters", "batch release parameter", errors);
                release = new PlantBatchRelease(Range(releaseItem, "labMinutes", releasePath, 1, 1440, errors), tests);
            }
        }
        PlantDeviations? deviations = null;
        if (item.TryGetProperty("deviations", out var deviationItem))
        {
            const string deviationPath = path + ".deviations";
            if (lines.All(l => l.Batch is null))
                errors.Add(new("plant.no_batch_line", deviationPath, "Deviations are raised for batches off the golden profile, and this plant has no batch line."));
            if (!item.TryGetProperty("batchRelease", out _))
                errors.Add(new("plant.batch_release_required", deviationPath, "A deviation holds a batch's release, so batches need release testing. Add quality.batchRelease."));
            if (qualityStaff.Count == 0)
                errors.Add(new("plant.qa_required", deviationPath, "Someone must approve deviations. Add people.qa with at least one QA person (id, name, shift)."));
            if (deviationItem.ValueKind != JsonValueKind.Object)
                errors.Add(new("plant.object_required", deviationPath, "Supply deviations as an object with closeHours, or leave it out."));
            else
            {
                SessionDefinitionLoader.CheckProperties(deviationItem, ["closeHours"], deviationPath, errors);
                deviations = new PlantDeviations(Range(deviationItem, "closeHours", deviationPath, 0.5, 720, errors));
            }
        }
        return new(parameters, Range(item, "labMinutes", path, 1, 1440, errors), Range(item, "holdMinutes", path, 10, 10080, errors), excursions)
        { BatchRelease = release, Deviations = deviations };
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
