namespace IndustrialDataSim.Core.Plant;

/// <summary>
/// Offline preview of a plant run for owner review: headline numbers, the
/// final state of every table (with its physical name), line segments,
/// failures, and tag samples on a coarse grid. Nothing is written anywhere.
/// </summary>
public static class PlantPreview
{
    public static object Build(PlantRun run, int sampleSeconds)
    {
        var signals = new PlantSignals(run);
        var tables = FinalTables(run);
        return new
        {
            simulation = run.Model.Simulation,
            startUtc = run.StartUtc,
            endUtc = run.EndUtc,
            anchorUtc = run.Model.AnchorUtc,
            openEnded = run.Model.EndUtc is null,
            sampleSeconds,
            summary = Summary(run, tables),
            tables = tables.ToDictionary(t => run.Model.TablePrefix + t.Key, t => t.Value),
            changeCount = run.Changes.Count,
            segments = run.Segments.Select(s => new { s.Line, s.Start, s.End, s.State, s.Sku, s.Order, s.Reason, s.Unit, batch = s.Batch?.BatchId, phase = s.Batch?.Phase }),
            failures = run.Failures,
            tags = signals.Tags.Select(tag => new
            {
                tag.Name, tag.ValueType, tag.Measure, tag.Line, tag.Equipment,
                points = Samples(signals, tag, run.StartUtc, run.EndUtc, sampleSeconds)
            }),
            notice = "Offline preview. Tag samples are thinned to the preview interval; the live plant samples at sampleIntervalMs."
        };
    }

    /// <summary>Applies every insert and update in order: each table's rows as a connector would see them at the end.</summary>
    public static Dictionary<string, List<Dictionary<string, object?>>> FinalTables(PlantRun run)
    {
        var tables = new Dictionary<string, Dictionary<string, Dictionary<string, object?>>>(StringComparer.Ordinal);
        foreach (var change in run.Changes)
        {
            var table = tables.TryGetValue(change.Table, out var existing) ? existing : tables[change.Table] = new(StringComparer.Ordinal);
            if (change.Operation == "insert") table[change.Key] = new(change.Columns);
            else foreach (var (column, value) in change.Columns) table[change.Key][column] = value;
        }
        return tables.OrderBy(t => t.Key, StringComparer.Ordinal)
            .ToDictionary(t => t.Key, t => t.Value.Values.ToList(), StringComparer.Ordinal);
    }

    private static List<object?[]> Samples(PlantSignals signals, PlantTag tag, DateTimeOffset start, DateTimeOffset end, int seconds)
    {
        var points = new List<object?[]>();
        object? previous = null;
        for (var t = start; t < end; t = t.AddSeconds(seconds))
        {
            var value = signals.ValueAt(tag, t);
            if (value is null) continue;
            // Strings change rarely; keep only changes to bound the preview size.
            if (value is string && Equals(value, previous)) continue;
            points.Add([t, value]);
            previous = value;
        }
        return points;
    }

    private static object Summary(PlantRun run, Dictionary<string, List<Dictionary<string, object?>>> tables)
    {
        List<Dictionary<string, object?>> Rows(string name) => tables.TryGetValue(name, out var rows) ? rows : [];
        var sales = Rows("salesorders");
        // Done means produced (no Shipping area) or shipped (with one).
        var completed = sales.Where(r => (string?)r["status"] is "Completed" or "Shipped").ToList();
        var tickets = Rows("maintenancetickets");
        var corrective = tickets.Where(t => (string?)t["ticket_type"] == "Corrective").ToList();
        double Minutes(Dictionary<string, object?> row, string from, string to) =>
            row[from] is DateTimeOffset a && row[to] is DateTimeOffset b ? (b - a).TotalMinutes : double.NaN;
        var downtime = Rows("downtimeevents");
        var lines = run.Model.Lines.Select(line =>
        {
            var segs = run.Segments.Where(s => s.Line == line.Id).ToList();
            double total = (run.EndUtc - run.StartUtc).TotalMinutes;
            double Of(string state) => segs.Where(s => s.State == state).Sum(s => (s.End - s.Start).TotalMinutes);
            return new
            {
                line = line.Id,
                runningPercent = Math.Round(100 * Of("Running") / total, 1),
                downPercent = Math.Round(100 * Of("Down") / total, 1),
                changeoverPercent = Math.Round(100 * Of("Changeover") / total, 1),
                maintenancePercent = Math.Round(100 * Of("Maintenance") / total, 1),
                minorStopPercent = Math.Round(100 * Of("MinorStop") / total, 1),
                idlePercent = Math.Round(100 * Of("Idle") / total, 1),
                minorStops = segs.Count(s => s.State == "MinorStop")
            };
        });
        return new
        {
            salesOrders = sales.Count,
            completedOrders = completed.Count,
            onTimePercent = completed.Count == 0 ? (double?)null : Math.Round(100.0 * completed.Count(r => r["on_time"] is true) / completed.Count, 1),
            breakdowns = run.Failures.Count,
            plantedBreakdowns = run.Failures.Count(f => f.Planted),
            correctiveTickets = corrective.Count,
            preventiveTickets = tickets.Count - corrective.Count,
            openTicketsAtEnd = tickets.Count(t => (string?)t["status"] != "Complete"),
            meanMinutesToAssign = Mean(corrective.Select(t => Minutes(t, "created_utc", "assigned_utc"))),
            meanMinutesToRepair = Mean(corrective.Select(t => Minutes(t, "started_utc", "completed_utc"))),
            qualitySamples = Rows("qualitysamples").Count,
            qualityHolds = Rows("qualityholds").Count,
            shipments = Rows("shipments").Count(r => (string?)r["status"] == "Shipped"),
            unplannedDowntimeMinutes = Math.Round(downtime.Where(d => (string?)d["category"] == "Unplanned").Sum(d => d["duration_minutes"] is double m ? m : 0), 1),
            lines,
            // Certification compliance (null without a certification section): every
            // safety-critical ticket assigned without a current certificate, and every
            // run started by an operator without one.
            uncertifiedAssignments = run.Model.Maintenance.Certification is null ? null : Rows("workcompliance").Where(r => r["certified"] is false)
                .Select(r => new { ticket = r["ticket_id"], technician = r["technician_id"], skill = r["skill"], reason = r["reason"], assignedUtc = r["checked_utc"],
                    failureCode = tickets.FirstOrDefault(t => Equals(t["ticket_id"], r["ticket_id"]))?["failure_code"] }).ToList(),
            uncertifiedRuns = run.Model.Maintenance.Certification?.Operators is null ? null : Rows("operatorcompliance").Where(r => r["certified"] is false)
                .Select(r =>
                {
                    var productionRun = Rows("productionruns").FirstOrDefault(p => Equals(p["run_id"], r["run_id"]));
                    return new { run = r["run_id"], line = productionRun?["line_id"], sku = productionRun?["sku"], operatorId = r["operator_id"], instruction = r["instruction_id"],
                        reason = r["reason"], startUtc = productionRun?["start_utc"], signoffUtc = r["signoff_utc"], goodCases = productionRun?["good_cases"], rejectCases = productionRun?["reject_cases"] };
                }).ToList(),
            // Batch units (null without one): batches run, and the mean cycle time of discharged batches per recipe.
            batches = run.Model.Lines.All(l => l.Batch is null) ? null : new
            {
                total = Rows("batches").Count,
                discharged = Rows("batches").Count(b => b["end_utc"] is DateTimeOffset),
                interruptedPhases = Rows("batchphases").Count(p => p["interrupted"] is true),
                recipes = run.Model.Lines.SelectMany(l => l.Batch?.Recipes ?? []).Select(recipe =>
                {
                    var done = Rows("batches").Where(b => Equals(b["recipe_id"], recipe.Id) && b["end_utc"] is DateTimeOffset).ToList();
                    return new { recipe = recipe.Id, sku = recipe.Sku, batches = done.Count, meanCycleMinutes = Mean(done.Select(b => Minutes(b, "start_utc", "end_utc"))) };
                }).ToList(),
                // Golden batch comparison: every discharged batch with its cycle time, yield and
                // deviation, and each drift's window (ended early by its cleaning PM when it has one).
                comparison = Rows("batches").Where(b => b["end_utc"] is DateTimeOffset).Select(b => new
                {
                    batch = b["batch_id"], line = b["line_id"], recipe = b["recipe_id"], startUtc = b["start_utc"],
                    cycleMinutes = Math.Round(Minutes(b, "start_utc", "end_utc"), 1), yieldPercent = b["yield_percent"],
                    deviationScore = b["deviation_score"], golden = b["golden"], status = b["status"], releasedUtc = b["released_utc"]
                }).ToList(),
                drifts = run.Model.Drifts.Select(d => new
                {
                    line = d.Line, recipe = d.Recipe, phase = d.Phase, cause = d.Cause, fromUtc = d.FromUtc,
                    untilUtc = new[] { d.UntilUtc, d.EndsWith is null ? null : tickets.Where(t => Equals(t["failure_code"], d.EndsWith) && t["completed_utc"] is DateTimeOffset done && done >= d.FromUtc)
                        .Select(t => (DateTimeOffset?)t["completed_utc"]).Min() }.Where(t => t is not null).Min(),
                    endsWith = d.EndsWith
                }).ToList()
            }
        };
    }

    private static double? Mean(IEnumerable<double> values)
    {
        var finite = values.Where(double.IsFinite).ToList();
        return finite.Count == 0 ? null : Math.Round(finite.Average(), 1);
    }
}
