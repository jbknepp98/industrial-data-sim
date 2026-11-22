namespace IndustrialDataSim.Core.Plant;

/// <summary>A plant tag: its full name, JSON value type and what it measures.</summary>
public sealed record PlantTag(string Name, string ValueType, string Measure, string Line, string? Equipment);

/// <summary>
/// Derives every tag value from the plant run's line segments and failures, so
/// the time series and the records come from one source of truth:
/// - a line's Speed is zero for the whole time it is Down, which is exactly the
///   condition the downtime detector uses;
/// - good and reject counts integrate the running segments, so they match the
///   production orders' quantities;
/// - a unit's vibration rises over WarningHours before each of its failures,
///   and a filler's fill volume drifts low, so the signals foreshadow tickets.
/// Values depend only on the run and the sample time (noise is stateless), so
/// any window of samples is reproducible.
/// </summary>
public sealed class PlantSignals
{
    private readonly PlantRun run;
    private readonly Dictionary<string, List<PlantLineSegment>> segmentsByLine;
    private readonly Dictionary<string, double[]> goodBefore, rejectBefore;
    private readonly List<(DateTimeOffset Utc, double Level)> stock = [];
    private readonly double stockBase;

    public PlantSignals(PlantRun run)
    {
        this.run = run;
        segmentsByLine = run.Model.Lines.ToDictionary(l => l.Id, l => run.Segments.Where(s => s.Line == l.Id).OrderBy(s => s.Start).ToList(), StringComparer.Ordinal);
        goodBefore = new(StringComparer.Ordinal);
        rejectBefore = new(StringComparer.Ordinal);
        foreach (var (line, list) in segmentsByLine)
        {
            // Cumulative output at the start of each segment, for O(log n) counts.
            var good = new double[list.Count + 1];
            var reject = new double[list.Count + 1];
            // A run resumed from a snapshot starts from the totals of every earlier segment.
            good[0] = run.Base?.Good.GetValueOrDefault(line) ?? 0;
            reject[0] = run.Base?.Reject.GetValueOrDefault(line) ?? 0;
            for (int i = 0; i < list.Count; i++)
            {
                double gross = list[i].CasesPerMinute * (list[i].End - list[i].Start).TotalMinutes;
                good[i + 1] = good[i] + gross * (1 - list[i].RejectPercent / 100);
                reject[i + 1] = reject[i] + gross * list[i].RejectPercent / 100;
            }
            goodBefore[line] = good;
            rejectBefore[line] = reject;
        }
        // Warehouse stock steps with each inventory transaction, in change order.
        double level = stockBase = run.Base?.Stock ?? 0;
        foreach (var change in run.Changes.Where(c => c.Table == "inventorytransactions" && c.Operation == "insert"))
        {
            level += System.Convert.ToDouble(change.Columns["quantity_cases"]);
            stock.Add((change.Utc, level));
        }
        Tags = BuildTags(run.Model);
    }

    public IReadOnlyList<PlantTag> Tags { get; }

    private static List<PlantTag> BuildTags(PlantModel model)
    {
        var tags = new List<PlantTag>();
        foreach (var line in model.Lines)
        {
            string prefix = $"{model.TagRoot}.{line.Id}";
            tags.Add(new($"{prefix}.State", "string", "LineState", line.Id, null));
            tags.Add(new($"{prefix}.SKU", "string", "Sku", line.Id, null));
            tags.Add(new($"{prefix}.OrderNumber", "string", "Order", line.Id, null));
            tags.Add(new($"{prefix}.Speed", "number", "Speed", line.Id, null));
            tags.Add(new($"{prefix}.GoodCount", "number", "GoodCount", line.Id, null));
            tags.Add(new($"{prefix}.RejectCount", "number", "RejectCount", line.Id, null));
            foreach (var unit in line.Equipment)
            {
                tags.Add(new($"{prefix}.{unit.Id}.Status", "string", "UnitStatus", line.Id, unit.Id));
                tags.Add(new($"{prefix}.{unit.Id}.Vibration", "number", "Vibration", line.Id, unit.Id));
                if (unit.Class == "filler") tags.Add(new($"{prefix}.{unit.Id}.FillVolume", "number", "FillVolume", line.Id, unit.Id));
            }
            // In-line analyzers: one per quality parameter, reading while the line runs.
            foreach (var parameter in model.Quality?.Parameters ?? [])
                tags.Add(new($"{prefix}.Quality.{parameter.Name}", "number", "Quality:" + parameter.Name, line.Id, null));
        }
        if (model.Shipping is not null)
            tags.Add(new($"{model.TagRoot}.Warehouse.CasesOnHand", "number", "CasesOnHand", "", null));
        return tags;
    }

    /// <summary>
    /// The tag's value at a sample time, or null when the tag emits no point then
    /// (fill volume exists only while the filler runs).
    /// </summary>
    public object? ValueAt(PlantTag tag, DateTimeOffset utc)
    {
        if (tag.Measure == "CasesOnHand")
        {
            if (utc < run.StartUtc) return null;
            int low = 0, high = stock.Count - 1, found = -1;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                if (stock[middle].Utc <= utc) { found = middle; low = middle + 1; }
                else high = middle - 1;
            }
            return found < 0 ? stockBase : stock[found].Level;
        }
        var list = segmentsByLine[tag.Line];
        int index = SegmentIndex(list, utc);
        if (index < 0) return null;
        var segment = list[index];
        bool running = segment.State == "Running";
        double noise = PlantRandom.Noise(run.Model.Seed, tag.Name, utc.UtcTicks);
        switch (tag.Measure)
        {
            case "LineState": return segment.State;
            case "Sku": return segment.Sku ?? "";
            case "Order": return segment.Order ?? "";
            case "Speed": return running ? Math.Round(segment.CasesPerMinute * (1 + 0.01 * noise), 2) : 0.0;
            case "GoodCount": return Math.Floor(Count(goodBefore[tag.Line], list, index, utc, good: true));
            case "RejectCount": return Math.Floor(Count(rejectBefore[tag.Line], list, index, utc, good: false));
            case "UnitStatus": return UnitStatus(segment, tag.Equipment!);
            case "Vibration":
            {
                double warning = WarningProgress(tag.Line, tag.Equipment!, utc);
                return running
                    ? Math.Round(2.5 + 4.0 * warning * warning + 0.15 * noise, 3)
                    : Math.Round(0.3 + 0.05 * Math.Abs(noise), 3); // stopped: ambient only
            }
            case "FillVolume":
            {
                if (!running) return null;
                var sku = run.Model.Skus.First(s => s.Code == segment.Sku);
                double warning = WarningProgress(tag.Line, tag.Equipment!, utc);
                return Math.Round(sku.FillTarget * (1 - 0.015 * warning + 0.002 * noise), 2);
            }
            case var measure when measure.StartsWith("Quality:", StringComparison.Ordinal):
            {
                if (!running || segment.Sku is null) return null;
                var parameter = run.Model.Quality!.Parameters.First(p => p.Name == measure["Quality:".Length..]);
                return PlantQualityValues.Value(run.Model, parameter, tag.Line, segment.Sku, utc);
            }
            default: throw new InvalidOperationException("Unknown plant measure.");
        }
    }

    private static string UnitStatus(PlantLineSegment segment, string unit) => segment.State switch
    {
        "Running" => "Running",
        "MinorStop" => "Blocked",
        "Down" => segment.Unit == unit ? "Faulted" : "Stopped",
        "Maintenance" => segment.Unit == unit ? "Maintenance" : "Stopped",
        "Changeover" => "Changeover",
        _ => "Idle"
    };

    private static double Count(double[] before, List<PlantLineSegment> list, int index, DateTimeOffset utc, bool good)
    {
        var segment = list[index];
        double gross = segment.CasesPerMinute * (utc - segment.Start).TotalMinutes;
        double share = good ? 1 - segment.RejectPercent / 100 : segment.RejectPercent / 100;
        return before[index] + gross * share;
    }

    /// <summary>
    /// 0 outside a warning window; rises from 0 to 1 over the WarningHours before
    /// the unit's next failure, and stays 1 until that failure is repaired.
    /// </summary>
    private double WarningProgress(string line, string unit, DateTimeOffset utc)
    {
        foreach (var failure in run.Failures)
        {
            if (failure.Line != line || failure.Equipment != unit || failure.WarningHours <= 0) continue;
            var warningStart = failure.FailedUtc.AddHours(-failure.WarningHours);
            if (utc < warningStart) continue;
            if (utc < failure.FailedUtc) return (utc - warningStart).TotalHours / failure.WarningHours;
            if (failure.RepairedUtc is null || utc < failure.RepairedUtc) return 1;
        }
        return 0;
    }

    private static int SegmentIndex(List<PlantLineSegment> list, DateTimeOffset utc)
    {
        int low = 0, high = list.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (utc < list[middle].Start) high = middle - 1;
            else if (utc >= list[middle].End) low = middle + 1;
            else return middle;
        }
        return -1;
    }
}
