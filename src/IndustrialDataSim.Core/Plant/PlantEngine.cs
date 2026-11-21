namespace IndustrialDataSim.Core.Plant;

/// <summary>
/// Discrete-event simulation of the Production → Maintenance loop on one clock:
///
///   demand → production orders → line schedule → line state (Running, Down …)
///   → failures detected after DetectionMinutes of stopped output → maintenance
///   ticket → technician (skill, shift, availability) → repair → line resumes.
///
/// Every record change and every line state comes from this single run, so the
/// relational data and the time series derived from the segments agree exactly.
/// All randomness comes from named PlantRandom streams; events that share a
/// timestamp run in (priority, insertion) order. The result depends only on the
/// model (including its anchor) and the end time.
/// </summary>
public static class PlantEngine
{
    public static PlantRun Run(PlantModel model, DateTimeOffset endUtc)
    {
        if (endUtc <= model.StartUtc) throw new ArgumentOutOfRangeException(nameof(endUtc), "End the run after the plant start.");
        if (model.EndUtc is { } stop && endUtc > stop) endUtc = stop;
        return new Simulation(model, endUtc).Execute();
    }

    // Same-time ordering: finish and repair before new failures, failures before
    // new work, so a line is never dispatched into a state that is about to change.
    private const int FinishPriority = 0, FailurePriority = 1, StatePriority = 2, PlanPriority = 3, AssignPriority = 4;

    private sealed class ProductionOrder
    {
        public required string Id, SalesOrder, Sku, Line;
        public required double GoodTarget;
        public required DateTimeOffset Created, Due;
        public double Good, Rejected;
        public DateTimeOffset? Started;
        public string? Run;
    }

    private sealed class Ticket
    {
        public required string Id, Type, Line, Equipment, Code, Skill, Priority;
        public required DateTimeOffset Created;
        public string Status = "Open";
        public string? Technician, Downtime;
        public PlantFailureMode? Mode;
        public PlantPreventive? Preventive;
        public int FailureIndex = -1;
        public bool Ready;           // corrective: always; preventive: once the line is handed over
        public int StatusCount;
    }

    private sealed class LineRuntime(PlantLine line)
    {
        public readonly PlantLine Line = line;
        public string State = "Idle";
        public DateTimeOffset StateStart;
        public string? Sku, Reason, Unit;
        public ProductionOrder? Current;
        public readonly List<ProductionOrder> Queue = [];
        public int Version;                       // bumps on each state change; stale events are ignored
        public DateTimeOffset RunningSince;
        public double Rate, Reject;
        public readonly Dictionary<string, double> HoursToFailure = new(StringComparer.Ordinal);
        public double HoursToMinorStop = double.PositiveInfinity;
        public readonly Queue<PlantFailureMode> PlantedFailures = new();
        public Ticket? PendingPreventive;
        public string? ChangeoverDowntime;
    }

    private sealed class TechnicianRuntime(PlantPerson person)
    {
        public readonly PlantPerson Person = person;
        public bool Busy;
        public int Completed;
    }

    private sealed class Simulation
    {
        private readonly PlantModel model;
        private readonly DateTimeOffset end;
        private readonly PriorityQueue<Action, (long Ticks, int Priority, long Order)> events = new();
        private long eventOrder, changeSequence;
        private DateTimeOffset now;
        private readonly List<PlantRecordChange> changes = [];
        private readonly List<PlantLineSegment> segments = [];
        private readonly List<PlantFailure> failures = [];
        private readonly Dictionary<string, LineRuntime> lines;
        private readonly List<TechnicianRuntime> technicians;
        private readonly List<Ticket> openTickets = [];
        private readonly Dictionary<string, int> counters = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PlantSku> skus;
        private readonly PlantRandom failureDraws, minorDraws;

        public Simulation(PlantModel model, DateTimeOffset end)
        {
            this.model = model;
            this.end = end;
            now = model.StartUtc;
            skus = model.Skus.ToDictionary(s => s.Code, StringComparer.Ordinal);
            lines = model.Lines.ToDictionary(l => l.Id, l => new LineRuntime(l) { StateStart = model.StartUtc }, StringComparer.Ordinal);
            technicians = model.Technicians.Select(t => new TechnicianRuntime(t)).ToList();
            failureDraws = new PlantRandom(model.Seed, "failures");
            minorDraws = new PlantRandom(model.Seed, "minor-stops");
        }

        public PlantRun Execute()
        {
            WriteReferenceData();
            foreach (var runtime in lines.Values)
            {
                // Draw each failure mode's first time-to-failure in declaration order.
                foreach (var mode in model.Maintenance.FailureModes.Where(m => m.Line == runtime.Line.Id))
                    runtime.HoursToFailure[mode.Code] = failureDraws.Exponential(mode.MtbfRunHours);
                runtime.HoursToMinorStop = NextMinorStopHours();
            }
            ScheduleDemand();
            ScheduleShiftStarts();
            SchedulePreventive();
            foreach (var incident in model.Incidents)
                At(incident.AtUtc, FailurePriority, () => PlantedIncident(incident));
            foreach (var runtime in lines.Values) At(model.StartUtc, StatePriority, () => Dispatch(runtime));

            while (events.TryPeek(out _, out var key) && key.Ticks < end.UtcTicks)
            {
                var action = events.Dequeue();
                now = new DateTimeOffset(key.Ticks, TimeSpan.Zero);
                action();
            }
            now = end;
            foreach (var runtime in lines.Values) CloseSegment(runtime);
            return new PlantRun(model, model.StartUtc, end, changes, segments.OrderBy(s => s.Start).ThenBy(s => s.Line, StringComparer.Ordinal).ToList(), failures);
        }

        /// <summary>
        /// Schedules an action. Event times round up to whole seconds, so every
        /// record timestamp is a tidy second and never earlier than its cause.
        /// </summary>
        private void At(DateTimeOffset when, int priority, Action action)
        {
            long ticks = when.UtcTicks;
            long remainder = ticks % TimeSpan.TicksPerSecond;
            if (remainder != 0) ticks += TimeSpan.TicksPerSecond - remainder;
            events.Enqueue(action, (ticks, priority, eventOrder++));
        }

        private string NextId(string prefix)
        {
            counters[prefix] = counters.GetValueOrDefault(prefix) + 1;
            return FormattableString.Invariant($"{prefix}-{counters[prefix]:000000}");
        }

        private void Record(string table, string key, string operation, Dictionary<string, object?> columns) =>
            changes.Add(new(++changeSequence, now, table, key, operation, columns));

        // ---------------- reference data ----------------

        private void WriteReferenceData()
        {
            foreach (var line in model.Lines)
            {
                Record("lines", line.Id, "insert", new() { ["line_id"] = line.Id, ["line_name"] = line.Name, ["changeover_minutes"] = line.ChangeoverMinutes });
                foreach (var unit in line.Equipment)
                    Record("equipment", line.Id + "." + unit.Id, "insert", new() { ["equipment_id"] = line.Id + "." + unit.Id, ["line_id"] = line.Id, ["equipment_name"] = unit.Name, ["equipment_class"] = unit.Class });
            }
            foreach (var sku in model.Skus)
                Record("skus", sku.Code, "insert", new() { ["sku"] = sku.Code, ["description"] = sku.Description, ["cases_per_minute"] = sku.CasesPerMinute, ["reject_percent"] = sku.RejectPercent, ["fill_target"] = sku.FillTarget, ["fill_unit"] = sku.FillUnit });
            foreach (var shift in model.Shifts)
                Record("shifts", shift.Name, "insert", new() { ["shift_name"] = shift.Name, ["start_hour_utc"] = shift.StartHour, ["hours"] = shift.Hours });
            foreach (var customer in model.Customers)
                Record("customers", customer.Id, "insert", new() { ["customer_id"] = customer.Id, ["customer_name"] = customer.Name });
            foreach (var person in model.Operators)
                Record("employees", person.Id, "insert", new() { ["employee_id"] = person.Id, ["employee_name"] = person.Name, ["role"] = "Operator", ["shift_name"] = person.Shift, ["line_id"] = person.Line });
            foreach (var person in model.Technicians)
            {
                Record("employees", person.Id, "insert", new() { ["employee_id"] = person.Id, ["employee_name"] = person.Name, ["role"] = "Technician", ["shift_name"] = person.Shift, ["line_id"] = null });
                foreach (var skill in person.Skills)
                    Record("certifications", person.Id + "." + skill, "insert", new() { ["certification_id"] = person.Id + "." + skill, ["employee_id"] = person.Id, ["skill"] = skill });
            }
            foreach (var part in model.Parts)
                Record("parts", part.Id, "insert", new() { ["part_id"] = part.Id, ["description"] = part.Description, ["unit_cost"] = part.UnitCost });
            foreach (var mode in model.Maintenance.FailureModes)
                Record("failurecodes", mode.Code, "insert", new() { ["failure_code"] = mode.Code, ["equipment_id"] = mode.Line + "." + mode.Equipment, ["description"] = mode.Description, ["skill"] = mode.Skill, ["priority"] = mode.Priority });
        }

        // ---------------- demand → orders ----------------

        private void ScheduleDemand()
        {
            // Start with about one day of open orders, so the lines have work at
            // the start. Older orders are treated as fulfilled before the plant's
            // history begins; replaying days of backlog at once would make every
            // early order late for reasons the story never shows.
            var backlogStart = model.StartUtc.AddDays(-1);
            var firstDay = backlogStart.UtcDateTime.Date;
            for (var day = firstDay; day < end.UtcDateTime; day = day.AddDays(1))
            {
                var random = new PlantRandom(model.Seed, $"demand:{day:yyyy-MM-dd}");
                int whole = (int)Math.Floor(model.Demand.OrdersPerDay);
                int count = whole + (random.NextDouble() < model.Demand.OrdersPerDay - whole ? 1 : 0);
                var placed = new List<(DateTimeOffset Created, Action Create)>();
                for (int i = 0; i < count; i++)
                {
                    var created = new DateTimeOffset(day.AddMinutes(Math.Floor(random.Uniform(0, 1440))), TimeSpan.Zero);
                    var sku = model.Skus[random.Integer(0, model.Skus.Count - 1)];
                    var customer = model.Customers[random.Integer(0, model.Customers.Count - 1)];
                    double cases = Math.Round(random.Uniform(model.Demand.CasesPerOrder) / 10) * 10;
                    var due = created.AddHours(Math.Round(random.Uniform(model.Demand.DueDays) * 24));
                    if (created < backlogStart) continue;
                    placed.Add((created, () => CreateOrder(created, sku, customer, Math.Max(10, cases), due)));
                }
                // Orders placed before the plant start are written at the start; their
                // created_utc column keeps the true (earlier) time.
                foreach (var (created, create) in placed.OrderBy(p => p.Created))
                    if (created < end) At(created < model.StartUtc ? model.StartUtc : created, PlanPriority, create);
            }
        }

        private void CreateOrder(DateTimeOffset created, PlantSku sku, PlantCustomer customer, double cases, DateTimeOffset due)
        {
            var capable = lines.Values.Where(l => l.Line.Skus.Contains(sku.Code)).ToList();
            if (capable.Count == 0) return; // a SKU no line can make receives no production order
            // Assign to the capable line with the least queued work; ties keep declaration order.
            var line = capable.OrderBy(l => l.Queue.Sum(o => o.GoodTarget - o.Good) + (l.Current is { } c ? c.GoodTarget - c.Good : 0)).First();
            string salesOrder = NextId("SO"), productionOrder = NextId("PO");
            Record("salesorders", salesOrder, "insert", new() { ["sales_order"] = salesOrder, ["customer_id"] = customer.Id, ["sku"] = sku.Code, ["quantity_cases"] = cases, ["created_utc"] = created, ["due_utc"] = due, ["status"] = "Open", ["completed_utc"] = null, ["on_time"] = null });
            Record("productionorders", productionOrder, "insert", new() { ["production_order"] = productionOrder, ["sales_order"] = salesOrder, ["sku"] = sku.Code, ["line_id"] = line.Line.Id, ["quantity_cases"] = cases, ["released_utc"] = now, ["due_utc"] = due, ["status"] = "Released", ["started_utc"] = null, ["completed_utc"] = null, ["good_cases"] = null, ["reject_cases"] = null });
            line.Queue.Add(new ProductionOrder { Id = productionOrder, SalesOrder = salesOrder, Sku = sku.Code, Line = line.Line.Id, GoodTarget = cases, Created = created, Due = due });
            if (line.State == "Idle") Dispatch(line);
        }

        // ---------------- line states ----------------

        private void SetState(LineRuntime line, string state, string? reason = null, string? unit = null)
        {
            CloseSegment(line);
            line.State = state;
            line.StateStart = now;
            line.Reason = reason;
            line.Unit = unit;
            line.Version++;
        }

        private void CloseSegment(LineRuntime line)
        {
            if (now <= line.StateStart) return;
            bool running = line.State == "Running";
            segments.Add(new(line.Line.Id, line.StateStart, now, line.State, line.Current?.Sku ?? line.Sku, line.Current?.Id,
                line.Reason, line.Unit, running ? line.Rate : 0, running ? line.Reject : 0));
            line.StateStart = now;
        }

        /// <summary>Starts the next work on an idle line: pending PM first, then the earliest-due order.</summary>
        private void Dispatch(LineRuntime line)
        {
            if (line.State != "Idle") return;
            if (line.PendingPreventive is { } pm)
            {
                BeginPreventive(line, pm);
                return;
            }
            if (line.Queue.Count == 0)
            {
                SetState(line, "Idle", "No released orders");
                return;
            }
            var next = line.Queue.OrderBy(o => o.Due).ThenBy(o => o.Created).ThenBy(o => o.Id, StringComparer.Ordinal).First();
            line.Queue.Remove(next);
            line.Current = next;
            if (line.Sku is not null && line.Sku != next.Sku && line.Line.ChangeoverMinutes > 0)
            {
                SetState(line, "Changeover", $"Changeover {line.Sku} to {next.Sku}");
                line.ChangeoverDowntime = NextId("DT");
                Record("downtimeevents", line.ChangeoverDowntime, "insert", Downtime(line.ChangeoverDowntime, line.Line.Id, null, "Planned", "Changeover", null, now, now, null));
                int version = line.Version;
                At(now.AddMinutes(line.Line.ChangeoverMinutes), FinishPriority, () =>
                {
                    if (line.Version != version) return;
                    Record("downtimeevents", line.ChangeoverDowntime!, "update", new() { ["end_utc"] = now, ["duration_minutes"] = line.Line.ChangeoverMinutes });
                    StartRunning(line);
                });
                return;
            }
            StartRunning(line);
        }

        private void StartRunning(LineRuntime line)
        {
            var order = line.Current!;
            var sku = skus[order.Sku];
            line.Sku = order.Sku;
            if (order.Started is null)
            {
                order.Started = now;
                order.Run = NextId("RUN");
                string? operatorId = model.Operators.FirstOrDefault(o => o.Line == line.Line.Id && o.Shift == ShiftAt(now).Name)?.Id;
                Record("productionruns", order.Run, "insert", new() { ["run_id"] = order.Run, ["production_order"] = order.Id, ["line_id"] = line.Line.Id, ["sku"] = order.Sku, ["operator_id"] = operatorId, ["start_utc"] = now, ["end_utc"] = null, ["good_cases"] = null, ["reject_cases"] = null });
                Record("productionorders", order.Id, "update", new() { ["status"] = "Running", ["started_utc"] = now });
                Record("salesorders", order.SalesOrder, "update", new() { ["status"] = "In Production" });
            }
            line.Rate = sku.CasesPerMinute;
            line.Reject = sku.RejectPercent;
            SetState(line, "Running", null, null);
            line.RunningSince = now;

            // The next stop is the earliest of: order complete, a breakdown, a minor
            // stop, or a planted incident waiting for the line to run.
            double goodPerMinute = line.Rate * (1 - line.Reject / 100);
            double minutesToComplete = Math.Max(0, (order.GoodTarget - order.Good) / goodPerMinute);
            string stop = "complete";
            double minutes = minutesToComplete;
            PlantFailureMode? failing = null;
            foreach (var mode in model.Maintenance.FailureModes.Where(m => m.Line == line.Line.Id))
            {
                double candidate = line.HoursToFailure[mode.Code] * 60;
                if (candidate < minutes) { minutes = candidate; stop = "failure"; failing = mode; }
            }
            if (line.HoursToMinorStop * 60 < minutes) { minutes = line.HoursToMinorStop * 60; stop = "minor"; }
            if (line.PlantedFailures.Count > 0 && 1 < minutes) { minutes = 1; stop = "planted"; }
            int version = line.Version;
            At(now.AddMinutes(minutes), stop == "complete" ? FinishPriority : FailurePriority, () =>
            {
                if (line.Version != version) return;
                LeaveRunning(line);
                switch (stop)
                {
                    case "complete": CompleteOrder(line); break;
                    case "failure":
                        line.HoursToFailure[failing!.Code] = failureDraws.Exponential(failing.MtbfRunHours);
                        Fail(line, failing, planted: false);
                        break;
                    case "minor": MinorStop(line); break;
                    default: Fail(line, line.PlantedFailures.Dequeue(), planted: true); break;
                }
            });
        }

        /// <summary>Accounts for output and running hours since the line started running.</summary>
        private void LeaveRunning(LineRuntime line)
        {
            double minutes = (now - line.RunningSince).TotalMinutes;
            double gross = line.Rate * minutes;
            var order = line.Current!;
            order.Good += gross * (1 - line.Reject / 100);
            order.Rejected += gross * line.Reject / 100;
            foreach (var key in line.HoursToFailure.Keys.ToList()) line.HoursToFailure[key] -= minutes / 60;
            line.HoursToMinorStop -= minutes / 60;
        }

        private void CompleteOrder(LineRuntime line)
        {
            var order = line.Current!;
            order.Good = order.GoodTarget; // floating-point remainder; output is whole cases
            long good = (long)Math.Round(order.Good), rejects = (long)Math.Round(order.Rejected);
            Record("productionruns", order.Run!, "update", new() { ["end_utc"] = now, ["good_cases"] = good, ["reject_cases"] = rejects });
            Record("productionorders", order.Id, "update", new() { ["status"] = "Completed", ["completed_utc"] = now, ["good_cases"] = good, ["reject_cases"] = rejects });
            Record("salesorders", order.SalesOrder, "update", new() { ["status"] = "Completed", ["completed_utc"] = now, ["on_time"] = now <= order.Due });
            line.Current = null;
            SetState(line, "Idle");
            Dispatch(line);
        }

        private void MinorStop(LineRuntime line)
        {
            line.HoursToMinorStop = NextMinorStopHours();
            // Cleared by the operator before the detector would flag it: visible in
            // the time series only, never as a downtime record or ticket.
            double minutes = minorDraws.Uniform(model.Maintenance.MinorStops.Minutes);
            SetState(line, "MinorStop", "Minor stop cleared by operator");
            int version = line.Version;
            At(now.AddMinutes(minutes), FinishPriority, () => { if (line.Version == version) StartRunning(line); });
        }

        private double NextMinorStopHours() => model.Maintenance.MinorStops.PerRunHour > 0
            ? minorDraws.Exponential(1 / model.Maintenance.MinorStops.PerRunHour) : double.PositiveInfinity;

        // ---------------- breakdowns: signals → records ----------------

        private void PlantedIncident(PlantIncident incident)
        {
            var mode = model.Maintenance.FailureModes.First(m => m.Code == incident.FailureMode);
            var line = lines[mode.Line];
            if (line.State == "Running")
            {
                LeaveRunning(line);
                Fail(line, mode, planted: true);
            }
            else line.PlantedFailures.Enqueue(mode); // fails shortly after the line next runs
        }

        private void Fail(LineRuntime line, PlantFailureMode mode, bool planted)
        {
            failures.Add(new(line.Line.Id, mode.Equipment, mode.Code, now, null, mode.WarningHours, planted));
            int index = failures.Count - 1;
            var failedAt = now;
            SetState(line, "Down", mode.Description, mode.Equipment);
            // Output is now zero. The detector rule is "line speed zero for
            // DetectionMinutes while work is scheduled"; the line stays Down until
            // repaired, so the rule fires exactly DetectionMinutes after the failure.
            At(now.AddMinutes(model.Maintenance.DetectionMinutes), FailurePriority, () =>
            {
                string downtime = NextId("DT");
                Record("downtimeevents", downtime, "insert", Downtime(downtime, line.Line.Id, line.Line.Id + "." + mode.Equipment, "Unplanned", mode.Description, mode.Code, failedAt, now, null));
                var ticket = new Ticket
                {
                    Id = NextId("MT"), Type = "Corrective", Line = line.Line.Id, Equipment = mode.Equipment, Code = mode.Code,
                    Skill = mode.Skill, Priority = mode.Priority, Created = now, Downtime = downtime, Mode = mode, FailureIndex = index, Ready = true
                };
                OpenTicket(ticket, $"Detected: {mode.Description}");
                TryAssign();
            });
        }

        // ---------------- maintenance ----------------

        private void SchedulePreventive()
        {
            foreach (var pm in model.Maintenance.Preventive)
            {
                var first = model.StartUtc.UtcDateTime.Date.AddHours(pm.StartHour);
                while (first < model.StartUtc.UtcDateTime) first = first.AddDays(1);
                for (var due = first; due < end.UtcDateTime; due = due.AddDays(pm.EveryDays))
                {
                    var when = new DateTimeOffset(due, TimeSpan.Zero);
                    At(when, PlanPriority, () => PreventiveDue(pm));
                }
            }
        }

        private void PreventiveDue(PlantPreventive pm)
        {
            var line = lines[pm.Line];
            if (line.PendingPreventive is not null) return; // the previous one has not run yet; do not stack
            var ticket = new Ticket
            {
                Id = NextId("MT"), Type = "Preventive", Line = pm.Line, Equipment = pm.Equipment, Code = pm.Code,
                Skill = pm.Skill, Priority = "Low", Created = now, Preventive = pm
            };
            OpenTicket(ticket, "Scheduled preventive maintenance");
            line.PendingPreventive = ticket;
            if (line.State == "Idle") Dispatch(line); // otherwise it starts when the current order finishes
        }

        private void BeginPreventive(LineRuntime line, Ticket ticket)
        {
            SetState(line, "Maintenance", ticket.Preventive!.Description, ticket.Equipment);
            ticket.Downtime = NextId("DT");
            Record("downtimeevents", ticket.Downtime, "insert", Downtime(ticket.Downtime, line.Line.Id, line.Line.Id + "." + ticket.Equipment, "Planned", "Preventive maintenance", ticket.Code, now, now, null));
            Record("maintenancetickets", ticket.Id, "update", new() { ["downtime_id"] = ticket.Downtime });
            ticket.Ready = true;
            TryAssign();
        }

        private void OpenTicket(Ticket ticket, string note)
        {
            openTickets.Add(ticket);
            Record("maintenancetickets", ticket.Id, "insert", new()
            {
                ["ticket_id"] = ticket.Id, ["ticket_type"] = ticket.Type, ["equipment_id"] = ticket.Line + "." + ticket.Equipment,
                ["failure_code"] = ticket.Code, ["priority"] = ticket.Priority, ["status"] = "Open", ["created_utc"] = now,
                ["downtime_id"] = ticket.Downtime, ["technician_id"] = null, ["assigned_utc"] = null, ["started_utc"] = null, ["completed_utc"] = null
            });
            StatusHistory(ticket, "Open", note);
        }

        private void StatusHistory(Ticket ticket, string status, string note)
        {
            ticket.Status = status;
            string key = FormattableString.Invariant($"{ticket.Id}-{++ticket.StatusCount:00}");
            Record("maintenanceticketstatus", key, "insert", new() { ["status_id"] = key, ["ticket_id"] = ticket.Id, ["status"] = status, ["status_utc"] = now, ["technician_id"] = ticket.Technician, ["note"] = note });
        }

        /// <summary>
        /// Gives waiting tickets to free technicians who are on shift and hold the
        /// skill. Highest priority first, then oldest; among technicians, the one
        /// with the fewest completed tickets, then by id. A ticket with no eligible
        /// technician waits for one to finish or for the next shift.
        /// </summary>
        private void TryAssign()
        {
            var shift = ShiftAt(now).Name;
            foreach (var ticket in openTickets.Where(t => t.Ready && t.Technician is null)
                         .OrderByDescending(t => PriorityRank(t.Priority)).ThenBy(t => t.Created).ThenBy(t => t.Id, StringComparer.Ordinal).ToList())
            {
                var technician = technicians.Where(t => !t.Busy && t.Person.Shift == shift && t.Person.Skills.Contains(ticket.Skill))
                    .OrderBy(t => t.Completed).ThenBy(t => t.Person.Id, StringComparer.Ordinal).FirstOrDefault();
                if (technician is null) continue;
                technician.Busy = true;
                ticket.Technician = technician.Person.Id;
                Record("maintenancetickets", ticket.Id, "update", new() { ["status"] = "Assigned", ["technician_id"] = ticket.Technician, ["assigned_utc"] = now });
                StatusHistory(ticket, "Assigned", "Assigned to technician");
                var random = new PlantRandom(model.Seed, "ticket:" + ticket.Id);
                double response = random.Uniform(model.Maintenance.ResponseMinutes);
                double work = ticket.Mode is { } mode ? random.Uniform(mode.RepairMinutes) : ticket.Preventive!.DurationMinutes;
                At(now.AddMinutes(response), StatePriority, () =>
                {
                    Record("maintenancetickets", ticket.Id, "update", new() { ["status"] = "In Progress", ["started_utc"] = now });
                    StatusHistory(ticket, "In Progress", "Technician on site");
                    At(now.AddMinutes(work), FinishPriority, () => CompleteTicket(ticket, technician));
                });
            }
        }

        private void CompleteTicket(Ticket ticket, TechnicianRuntime technician)
        {
            Record("maintenancetickets", ticket.Id, "update", new() { ["status"] = "Complete", ["completed_utc"] = now });
            StatusHistory(ticket, "Complete", ticket.Type == "Corrective" ? "Repaired; line returned to service" : "Preventive maintenance done");
            foreach (var use in ticket.Mode?.Parts ?? [])
            {
                string key = NextId("PU");
                Record("partsusage", key, "insert", new() { ["usage_id"] = key, ["ticket_id"] = ticket.Id, ["part_id"] = use.Part, ["quantity"] = use.Quantity, ["used_utc"] = now });
            }
            technician.Busy = false;
            technician.Completed++;
            openTickets.Remove(ticket);
            var line = lines[ticket.Line];
            Record("downtimeevents", ticket.Downtime!, "update", new() { ["end_utc"] = now, ["duration_minutes"] = Math.Round((now - DowntimeStart(ticket)).TotalMinutes, 1) });
            if (ticket.Type == "Corrective")
            {
                var failure = failures[ticket.FailureIndex];
                failures[ticket.FailureIndex] = failure with { RepairedUtc = now };
                // Back in service: resume the interrupted order, or take new work.
                if (line.Current is not null) StartRunning(line);
                else { SetState(line, "Idle"); Dispatch(line); }
            }
            else
            {
                line.PendingPreventive = null;
                SetState(line, "Idle");
                Dispatch(line);
            }
            TryAssign();
        }

        private DateTimeOffset DowntimeStart(Ticket ticket) => ticket.FailureIndex >= 0 ? failures[ticket.FailureIndex].FailedUtc
            : (DateTimeOffset)changes.First(c => c.Table == "downtimeevents" && c.Key == ticket.Downtime && c.Operation == "insert").Columns["start_utc"]!;

        private void ScheduleShiftStarts()
        {
            for (var day = model.StartUtc.UtcDateTime.Date; day < end.UtcDateTime; day = day.AddDays(1))
                foreach (var shift in model.Shifts)
                {
                    var when = new DateTimeOffset(day.AddHours(shift.StartHour), TimeSpan.Zero);
                    if (when > model.StartUtc && when < end) At(when, AssignPriority, TryAssign);
                }
        }

        private PlantShift ShiftAt(DateTimeOffset utc)
        {
            int hour = utc.UtcDateTime.Hour;
            return model.Shifts.First(s => (hour - s.StartHour + 24) % 24 < s.Hours);
        }

        private static int PriorityRank(string priority) => priority switch { "Critical" => 4, "High" => 3, "Medium" => 2, _ => 1 };

        private static Dictionary<string, object?> Downtime(string id, string line, string? equipment, string category, string reason, string? code,
            DateTimeOffset start, DateTimeOffset detected, DateTimeOffset? endUtc) => new()
        {
            ["downtime_id"] = id, ["line_id"] = line, ["equipment_id"] = equipment, ["category"] = category, ["reason"] = reason,
            ["failure_code"] = code, ["start_utc"] = start, ["detected_utc"] = detected, ["end_utc"] = endUtc, ["duration_minutes"] = null
        };
    }
}
