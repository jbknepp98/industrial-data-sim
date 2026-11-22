using System.Text.Json;
using System.Text.Json.Serialization;

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
///
/// Snapshots: pending events are plain data, not closures, so the complete
/// engine state at any instant can be saved (Capture) and resumed (Restore). A
/// resumed run continues exactly as a run from the plant start would: the same
/// change numbers, segments, failures and random draws. Calendar events
/// (demand, shift starts, preventive maintenance) are generated a day at a time
/// as the clock reaches them, with ordering keys that reproduce the original
/// "schedule everything up front" order, so a run's cost no longer depends on
/// how far ahead its calendar reaches.
/// </summary>
public static class PlantEngine
{
    /// <summary>
    /// Bump whenever the engine's behaviour or snapshot layout changes. A stored
    /// snapshot from another version is discarded and the plant replays from its
    /// start, which is always correct, only slower.
    /// </summary>
    public const int EngineVersion = 1;

    public static PlantRun Run(PlantModel model, DateTimeOffset endUtc) => Run(model, endUtc, null, null).Run;

    /// <summary>
    /// Runs from the plant start, or from a snapshot, to endUtc. With
    /// snapshotAtUtc set (strictly between the run's start and end), also
    /// captures the engine state at that instant.
    /// </summary>
    public static PlantRunResult Run(PlantModel model, DateTimeOffset endUtc, PlantSnapshot? from, DateTimeOffset? snapshotAtUtc)
    {
        var start = from?.AtUtc ?? model.StartUtc;
        if (endUtc <= model.StartUtc) throw new ArgumentOutOfRangeException(nameof(endUtc), "End the run after the plant start.");
        if (model.EndUtc is { } stop && endUtc > stop) endUtc = stop;
        if (endUtc <= start) throw new ArgumentOutOfRangeException(nameof(endUtc), "End the run after the snapshot.");
        if (snapshotAtUtc is { } at && (at <= start || at >= endUtc))
            throw new ArgumentOutOfRangeException(nameof(snapshotAtUtc), "Capture a snapshot strictly between the run's start and end.");
        var simulation = from is null ? new Simulation(model, endUtc) : Simulation.Restore(model, endUtc, from);
        return simulation.Execute(snapshotAtUtc);
    }

    // Same-time ordering: finish and repair before new failures, failures before
    // new work, so a line is never dispatched into a state that is about to change.
    private const int FinishPriority = 0, FailurePriority = 1, StatePriority = 2, PlanPriority = 3, AssignPriority = 4;

    // Calendar events keep the order they had when everything was scheduled up
    // front: below every dynamic event (order ≥ 0), grouped as demand, shift
    // starts, preventive maintenance, incidents, then the initial dispatches.
    private const int DemandGroup = 0, ShiftGroup = 1, PreventiveGroup = 2, IncidentGroup = 3, DispatchGroup = 4;

    private static long CalendarOrder(int group, long major, long minor) =>
        long.MinValue + ((long)group << 58) + (major << 20) + minor;

    private enum Kind
    {
        Dispatch, Incident, Order, ShiftStart, PreventiveDue, ChangeoverEnd, RunStop, MinorStopEnd,
        LabResult, HoldRelease, Ship, Detect, TechnicianArrives, TicketComplete
    }

    /// <summary>A pending event as data. Which fields are used depends on Kind.</summary>
    private sealed record Event(Kind Kind, string? Line = null, int Version = 0, int Index = 0, string? Id = null,
        string? Ref = null, string? Ref2 = null, double Number = 0, DateTimeOffset? Time = null, DateTimeOffset? Time2 = null,
        OrderRef? Order = null);

    /// <summary>What a lot, lab result or shipment needs to know about its finished production order.</summary>
    private sealed record OrderRef(string Id, string SalesOrder, string Sku, DateTimeOffset Due);

    private sealed class ProductionOrder
    {
        public required string Id { get; init; }
        public required string SalesOrder { get; init; }
        public required string Sku { get; init; }
        public required string Line { get; init; }
        public required double GoodTarget { get; init; }
        public required DateTimeOffset Created { get; init; }
        public required DateTimeOffset Due { get; init; }
        public double Good { get; set; }
        public double Rejected { get; set; }
        public DateTimeOffset? Started { get; set; }
        public string? Run { get; set; }

        public OrderRef Ref() => new(Id, SalesOrder, Sku, Due);
    }

    private sealed class Ticket
    {
        public required string Id { get; init; }
        public required string Type { get; init; }
        public required string Line { get; init; }
        public required string Equipment { get; init; }
        public required string Code { get; init; }
        public required string Skill { get; init; }
        public required string Priority { get; init; }
        public required DateTimeOffset Created { get; init; }
        public string Status { get; set; } = "Open";
        public string? Technician { get; set; }
        public string? Downtime { get; set; }
        public DateTimeOffset? DowntimeStart { get; set; }
        public string? ModeCode { get; init; }       // corrective: the failure mode
        public int PreventiveIndex { get; init; } = -1; // preventive: index into the model's preventive list
        public int FailureNumber { get; init; } = -1;
        public bool Ready { get; set; }              // corrective: always; preventive: once the line is handed over
        public int StatusCount { get; set; }
    }

    private sealed class LineRuntime(PlantLine line)
    {
        public readonly PlantLine Line = line;
        public string State = "Idle";
        public DateTimeOffset StateStart;
        public string? Sku, Reason, Unit;
        public ProductionOrder? Current;
        public List<ProductionOrder> Queue = [];
        public int Version;                       // bumps on each state change; stale events are ignored
        public DateTimeOffset RunningSince;
        public double Rate, Reject;
        public Dictionary<string, double> HoursToFailure = new(StringComparer.Ordinal);
        public double HoursToMinorStop = double.PositiveInfinity;
        public Queue<string> PlantedFailures = new(); // failure mode codes
        public Ticket? PendingPreventive;
        public string? ChangeoverDowntime;
        public double GoodTotal, RejectTotal;      // over closed segments, for the count signals
    }

    private sealed class TechnicianRuntime(PlantPerson person)
    {
        public readonly PlantPerson Person = person;
        public bool Busy;
        public int Completed;
    }

    // ---------------- snapshot layout (EngineVersion 1) ----------------

    private sealed record StateDto(
        long EventOrder, long ChangeSequence, long FailureCount, double Stock, DateTimeOffset CalendarUntil,
        DateTime[] PreventiveNextDue, int[] PreventiveDueIndex, ulong FailureDraws, ulong MinorDraws,
        Dictionary<string, int> Counters, List<LineDto> Lines, List<TechnicianDto> Technicians,
        List<Ticket> OpenTickets, List<FailureDto> Failures, List<EventDto> Events);

    private sealed record LineDto(string Id, string State, DateTimeOffset StateStart, string? Sku, string? Reason, string? Unit,
        ProductionOrder? Current, List<ProductionOrder> Queue, int Version, DateTimeOffset RunningSince, double Rate, double Reject,
        Dictionary<string, double> HoursToFailure, double HoursToMinorStop, List<string> PlantedFailures,
        string? PendingPreventive, string? ChangeoverDowntime, double GoodTotal, double RejectTotal);

    private sealed record TechnicianDto(string Id, bool Busy, int Completed);

    private sealed record FailureDto(long Number, PlantFailure Failure);

    private sealed record EventDto(long Ticks, int Priority, long Order, Event Event);

    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, // HoursToMinorStop may be +∞
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class Simulation
    {
        private readonly PlantModel model;
        private readonly DateTimeOffset end;
        private readonly PriorityQueue<Event, (long Ticks, int Priority, long Order)> events = new();
        private long eventOrder, changeSequence, failureCount;
        private DateTimeOffset now;
        private readonly DateTimeOffset runStart;
        private readonly PlantRunBase? runBase;
        private readonly List<PlantRecordChange> changes = [];
        private readonly List<PlantLineSegment> segments = [];
        private readonly List<(long Number, PlantFailure Failure)> failures = [];
        private readonly Dictionary<string, LineRuntime> lines;
        private readonly List<TechnicianRuntime> technicians;
        private List<Ticket> openTickets = [];
        private Dictionary<string, int> counters = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PlantSku> skus;
        private readonly PlantRandom failureDraws, minorDraws;
        private double stock;
        private DateTimeOffset calendarUntil;      // calendar events before this instant are queued
        private DateTime[] preventiveNextDue;
        private int[] preventiveDueIndex;
        private bool fresh = true;

        public Simulation(PlantModel model, DateTimeOffset end)
        {
            this.model = model;
            this.end = end;
            now = runStart = model.StartUtc;
            skus = model.Skus.ToDictionary(s => s.Code, StringComparer.Ordinal);
            lines = model.Lines.ToDictionary(l => l.Id, l => new LineRuntime(l) { StateStart = model.StartUtc }, StringComparer.Ordinal);
            technicians = model.Technicians.Select(t => new TechnicianRuntime(t)).ToList();
            failureDraws = new PlantRandom(model.Seed, "failures");
            minorDraws = new PlantRandom(model.Seed, "minor-stops");
            calendarUntil = new DateTimeOffset(model.StartUtc.AddDays(-1).UtcDateTime.Date, TimeSpan.Zero);
            preventiveNextDue = model.Maintenance.Preventive.Select(pm =>
            {
                var first = model.StartUtc.UtcDateTime.Date.AddHours(pm.StartHour);
                while (first < model.StartUtc.UtcDateTime) first = first.AddDays(1);
                return first;
            }).ToArray();
            preventiveDueIndex = new int[model.Maintenance.Preventive.Count];
        }

        private Simulation(PlantModel model, DateTimeOffset end, DateTimeOffset at, PlantRunBase runBase) : this(model, end)
        {
            now = runStart = at;
            this.runBase = runBase;
            fresh = false;
        }

        public static Simulation Restore(PlantModel model, DateTimeOffset end, PlantSnapshot snapshot)
        {
            if (snapshot.EngineVersion != EngineVersion)
                throw new PlantSnapshotException($"The snapshot was written by engine version {snapshot.EngineVersion}; this engine is version {EngineVersion}.");
            StateDto state;
            try { state = JsonSerializer.Deserialize<StateDto>(snapshot.State, SnapshotJson) ?? throw new JsonException("empty"); }
            catch (JsonException) { throw new PlantSnapshotException("The snapshot state is not valid snapshot JSON."); }
            if (state.ChangeSequence != snapshot.ChangeSequence || state.Lines.Count != model.Lines.Count ||
                state.PreventiveNextDue.Length != model.Maintenance.Preventive.Count || state.Technicians.Count != model.Technicians.Count)
                throw new PlantSnapshotException("The snapshot does not match this plant model.");

            var runBase = new PlantRunBase(state.Lines.ToDictionary(l => l.Id, l => l.GoodTotal, StringComparer.Ordinal),
                state.Lines.ToDictionary(l => l.Id, l => l.RejectTotal, StringComparer.Ordinal), state.Stock);
            var simulation = new Simulation(model, end, snapshot.AtUtc, runBase);
            simulation.Load(state);
            return simulation;
        }

        private void Load(StateDto state)
        {
            eventOrder = state.EventOrder;
            changeSequence = state.ChangeSequence;
            failureCount = state.FailureCount;
            stock = state.Stock;
            calendarUntil = state.CalendarUntil;
            preventiveNextDue = state.PreventiveNextDue;
            preventiveDueIndex = state.PreventiveDueIndex;
            failureDraws.State = state.FailureDraws;
            minorDraws.State = state.MinorDraws;
            counters = new Dictionary<string, int>(state.Counters, StringComparer.Ordinal);
            openTickets = state.OpenTickets;
            var ticketsById = openTickets.ToDictionary(t => t.Id, StringComparer.Ordinal);
            foreach (var dto in state.Lines)
            {
                if (!lines.TryGetValue(dto.Id, out var line)) throw new PlantSnapshotException("The snapshot names a line this plant model does not have.");
                line.State = dto.State;
                line.StateStart = dto.StateStart;
                line.Sku = dto.Sku;
                line.Reason = dto.Reason;
                line.Unit = dto.Unit;
                line.Current = dto.Current;
                line.Queue = dto.Queue;
                line.Version = dto.Version;
                line.RunningSince = dto.RunningSince;
                line.Rate = dto.Rate;
                line.Reject = dto.Reject;
                line.HoursToFailure = new Dictionary<string, double>(dto.HoursToFailure, StringComparer.Ordinal);
                line.HoursToMinorStop = dto.HoursToMinorStop;
                line.PlantedFailures = new Queue<string>(dto.PlantedFailures);
                line.PendingPreventive = dto.PendingPreventive is null ? null
                    : ticketsById.GetValueOrDefault(dto.PendingPreventive) ?? throw new PlantSnapshotException("The snapshot's pending preventive ticket is missing.");
                line.ChangeoverDowntime = dto.ChangeoverDowntime;
                line.GoodTotal = dto.GoodTotal;
                line.RejectTotal = dto.RejectTotal;
            }
            foreach (var (technician, dto) in technicians.Zip(state.Technicians))
            {
                if (technician.Person.Id != dto.Id) throw new PlantSnapshotException("The snapshot's technicians do not match this plant model.");
                technician.Busy = dto.Busy;
                technician.Completed = dto.Completed;
            }
            foreach (var failure in state.Failures) failures.Add((failure.Number, failure.Failure));
            foreach (var pending in state.Events) events.Enqueue(pending.Event, (pending.Ticks, pending.Priority, pending.Order));
        }

        private PlantSnapshot Capture(DateTimeOffset at)
        {
            var state = new StateDto(
                eventOrder, changeSequence, failureCount, stock, calendarUntil,
                (DateTime[])preventiveNextDue.Clone(), (int[])preventiveDueIndex.Clone(), failureDraws.State, minorDraws.State,
                new Dictionary<string, int>(counters, StringComparer.Ordinal),
                lines.Values.Select(l => new LineDto(l.Line.Id, l.State, l.StateStart, l.Sku, l.Reason, l.Unit, l.Current, l.Queue,
                    l.Version, l.RunningSince, l.Rate, l.Reject, l.HoursToFailure, l.HoursToMinorStop, l.PlantedFailures.ToList(),
                    l.PendingPreventive?.Id, l.ChangeoverDowntime, l.GoodTotal, l.RejectTotal)).ToList(),
                technicians.Select(t => new TechnicianDto(t.Person.Id, t.Busy, t.Completed)).ToList(),
                openTickets,
                // Failures repaired before the snapshot no longer affect any signal
                // or record at or after it; open ones are kept for tickets and vibration.
                failures.Where(f => f.Failure.RepairedUtc is null || f.Failure.RepairedUtc >= at)
                    .Select(f => new FailureDto(f.Number, f.Failure)).ToList(),
                events.UnorderedItems.Select(e => new EventDto(e.Priority.Ticks, e.Priority.Priority, e.Priority.Order, e.Element))
                    .OrderBy(e => e.Ticks).ThenBy(e => e.Priority).ThenBy(e => e.Order).ToList());
            return new PlantSnapshot(EngineVersion, at, changeSequence, JsonSerializer.Serialize(state, SnapshotJson));
        }

        public PlantRunResult Execute(DateTimeOffset? snapshotAt)
        {
            if (fresh)
            {
                WriteReferenceData();
                foreach (var runtime in lines.Values)
                {
                    // Draw each failure mode's first time-to-failure in declaration order.
                    foreach (var mode in model.Maintenance.FailureModes.Where(m => m.Line == runtime.Line.Id))
                        runtime.HoursToFailure[mode.Code] = failureDraws.Exponential(mode.MtbfRunHours);
                    runtime.HoursToMinorStop = NextMinorStopHours();
                }
                for (int i = 0; i < model.Incidents.Count; i++)
                    Calendar(model.Incidents[i].AtUtc, FailurePriority, CalendarOrder(IncidentGroup, 0, i), new Event(Kind.Incident, Index: i));
                int lineIndex = 0;
                foreach (var runtime in lines.Values)
                    Calendar(model.StartUtc, StatePriority, CalendarOrder(DispatchGroup, 0, lineIndex++), new Event(Kind.Dispatch, Line: runtime.Line.Id));
            }

            PlantSnapshot? snapshot = null;
            while (true)
            {
                long next = NextTicks();
                // Queue the calendar of every day the clock is about to enter.
                while (calendarUntil.UtcTicks <= Math.Min(next, end.UtcTicks))
                {
                    ScheduleCalendarDay(calendarUntil);
                    calendarUntil = calendarUntil.AddDays(1);
                    next = NextTicks();
                }
                if (snapshotAt is { } at && snapshot is null && next >= at.UtcTicks) snapshot = Capture(at);
                if (next >= end.UtcTicks) break;
                events.TryDequeue(out var pending, out var key);
                now = new DateTimeOffset(key.Ticks, TimeSpan.Zero);
                Handle(pending!);
            }
            now = end;
            foreach (var runtime in lines.Values) CloseSegment(runtime);
            var run = new PlantRun(model, runStart, end, changes, segments.OrderBy(s => s.Start).ThenBy(s => s.Line, StringComparer.Ordinal).ToList(),
                failures.Select(f => f.Failure).ToList()) { Base = runBase };
            return new PlantRunResult(run, snapshot);
        }

        private long NextTicks() => events.TryPeek(out _, out var key) ? key.Ticks : long.MaxValue;

        private void Handle(Event e)
        {
            switch (e.Kind)
            {
                case Kind.Dispatch: Dispatch(lines[e.Line!]); break;
                case Kind.Incident: PlantedIncident(model.Incidents[e.Index]); break;
                case Kind.Order:
                    CreateOrder(e.Time!.Value, skus[e.Ref!], model.Customers.First(c => c.Id == e.Ref2), e.Number, e.Time2!.Value);
                    break;
                case Kind.ShiftStart: TryAssign(); break;
                case Kind.PreventiveDue: PreventiveDue(e.Index); break;
                case Kind.ChangeoverEnd:
                {
                    var line = lines[e.Line!];
                    if (line.Version != e.Version) return;
                    Record("downtimeevents", line.ChangeoverDowntime!, "update", new() { ["end_utc"] = now, ["duration_minutes"] = line.Line.ChangeoverMinutes });
                    StartRunning(line);
                    break;
                }
                case Kind.RunStop: RunStop(lines[e.Line!], e.Version, e.Id!, e.Ref); break;
                case Kind.MinorStopEnd:
                {
                    var line = lines[e.Line!];
                    if (line.Version == e.Version) StartRunning(line);
                    break;
                }
                case Kind.LabResult: LabResult(e.Id!, e.Ref!, e.Line!, e.Order!, e.Time!.Value, (long)e.Number); break;
                case Kind.HoldRelease:
                    Record("qualityholds", e.Id!, "update", new() { ["released_utc"] = now, ["disposition"] = "Released after investigation and retest" });
                    Record("lots", e.Ref!, "update", new() { ["status"] = "Released", ["released_utc"] = now });
                    Release(e.Ref!, e.Order!, (long)e.Number);
                    break;
                case Kind.Ship: Ship(e.Id!, e.Ref!, e.Order!, (long)e.Number); break;
                case Kind.Detect: Detect(lines[e.Line!], Mode(e.Ref!), e.Time!.Value, e.Index); break;
                case Kind.TechnicianArrives:
                {
                    var ticket = OpenTicketById(e.Id!);
                    Record("maintenancetickets", ticket.Id, "update", new() { ["status"] = "In Progress", ["started_utc"] = now });
                    StatusHistory(ticket, "In Progress", "Technician on site");
                    At(now.AddMinutes(e.Number), FinishPriority, new Event(Kind.TicketComplete, Id: ticket.Id, Ref: e.Ref));
                    break;
                }
                case Kind.TicketComplete: CompleteTicket(OpenTicketById(e.Id!), technicians.First(t => t.Person.Id == e.Ref)); break;
                default: throw new InvalidOperationException("Unknown plant event.");
            }
        }

        private PlantFailureMode Mode(string code) => model.Maintenance.FailureModes.First(m => m.Code == code);

        private Ticket OpenTicketById(string id) => openTickets.First(t => t.Id == id);

        /// <summary>
        /// Schedules a dynamic event. Event times round up to whole seconds, so
        /// every record timestamp is a tidy second and never earlier than its cause.
        /// </summary>
        private void At(DateTimeOffset when, int priority, Event e) => events.Enqueue(e, (WholeSecondTicks(when), priority, eventOrder++));

        private void Calendar(DateTimeOffset when, int priority, long order, Event e) => events.Enqueue(e, (WholeSecondTicks(when), priority, order));

        private static long WholeSecondTicks(DateTimeOffset when)
        {
            long ticks = when.UtcTicks;
            long remainder = ticks % TimeSpan.TicksPerSecond;
            if (remainder != 0) ticks += TimeSpan.TicksPerSecond - remainder;
            return ticks;
        }

        private string NextId(string prefix)
        {
            counters[prefix] = counters.GetValueOrDefault(prefix) + 1;
            return FormattableString.Invariant($"{prefix}-{counters[prefix]:000000}");
        }

        private void Record(string table, string key, string operation, Dictionary<string, object?> columns)
        {
            changes.Add(new(++changeSequence, now, table, key, operation, columns));
            if (table == "inventorytransactions" && operation == "insert") stock += System.Convert.ToDouble(columns["quantity_cases"]);
        }

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
            foreach (var parameter in model.Quality?.Parameters ?? [])
                foreach (var sku in model.Skus)
                {
                    string spec = parameter.Name + "." + sku.Code;
                    Record("qualityspecs", spec, "insert", new() { ["spec_id"] = spec, ["parameter"] = parameter.Name, ["sku"] = sku.Code, ["unit"] = parameter.Unit, ["target"] = parameter.Target, ["lower_limit"] = parameter.Lower, ["upper_limit"] = parameter.Upper });
                }
            foreach (var mode in model.Maintenance.FailureModes)
                Record("failurecodes", mode.Code, "insert", new() { ["failure_code"] = mode.Code, ["equipment_id"] = mode.Line + "." + mode.Equipment, ["description"] = mode.Description, ["skill"] = mode.Skill, ["priority"] = mode.Priority });
        }

        // ---------------- the calendar, one day at a time ----------------

        /// <summary>
        /// Queues the demand, shift starts and preventive maintenance due on one
        /// UTC day. Each source has its own random stream or fixed rule, so the
        /// day's events do not depend on when they are queued.
        /// </summary>
        private void ScheduleCalendarDay(DateTimeOffset dayStart)
        {
            var day = dayStart.UtcDateTime;
            ScheduleDemand(day);
            var firstShiftDay = model.StartUtc.UtcDateTime.Date;
            if (day >= firstShiftDay)
            {
                long dayIndex = (long)(day - firstShiftDay).TotalDays;
                for (int i = 0; i < model.Shifts.Count; i++)
                {
                    var when = new DateTimeOffset(day.AddHours(model.Shifts[i].StartHour), TimeSpan.Zero);
                    if (when > model.StartUtc) Calendar(when, AssignPriority, CalendarOrder(ShiftGroup, dayIndex, i), new Event(Kind.ShiftStart));
                }
            }
            var dayEnd = day.AddDays(1);
            for (int i = 0; i < model.Maintenance.Preventive.Count; i++)
            {
                var pm = model.Maintenance.Preventive[i];
                for (; preventiveNextDue[i] < dayEnd; preventiveNextDue[i] = preventiveNextDue[i].AddDays(pm.EveryDays), preventiveDueIndex[i]++)
                    Calendar(new DateTimeOffset(preventiveNextDue[i], TimeSpan.Zero), PlanPriority,
                        CalendarOrder(PreventiveGroup, ((long)i << 24) + preventiveDueIndex[i], 0), new Event(Kind.PreventiveDue, Index: i));
            }
        }

        // ---------------- demand → orders ----------------

        private void ScheduleDemand(DateTime day)
        {
            // Start with about one day of open orders, so the lines have work at
            // the start. Older orders are treated as fulfilled before the plant's
            // history begins; replaying days of backlog at once would make every
            // early order late for reasons the story never shows.
            var backlogStart = model.StartUtc.AddDays(-1);
            var firstDay = backlogStart.UtcDateTime.Date;
            long dayIndex = (long)(day - firstDay).TotalDays;
            var random = new PlantRandom(model.Seed, $"demand:{day:yyyy-MM-dd}");
            int whole = (int)Math.Floor(model.Demand.OrdersPerDay);
            int count = whole + (random.NextDouble() < model.Demand.OrdersPerDay - whole ? 1 : 0);
            var placed = new List<(DateTimeOffset Created, Event Create)>();
            for (int i = 0; i < count; i++)
            {
                var created = new DateTimeOffset(day.AddMinutes(Math.Floor(random.Uniform(0, 1440))), TimeSpan.Zero);
                var sku = model.Skus[random.Integer(0, model.Skus.Count - 1)];
                var customer = model.Customers[random.Integer(0, model.Customers.Count - 1)];
                double cases = Math.Round(random.Uniform(model.Demand.CasesPerOrder) / 10) * 10;
                var due = created.AddHours(Math.Round(random.Uniform(model.Demand.DueDays) * 24));
                if (created < backlogStart) continue;
                placed.Add((created, new Event(Kind.Order, Ref: sku.Code, Ref2: customer.Id, Number: Math.Max(10, cases), Time: created, Time2: due)));
            }
            // Orders placed before the plant start are written at the start; their
            // created_utc column keeps the true (earlier) time.
            int position = 0;
            foreach (var (created, create) in placed.OrderBy(p => p.Created))
                Calendar(created < model.StartUtc ? model.StartUtc : created, PlanPriority, CalendarOrder(DemandGroup, dayIndex, position++), create);
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
            var segment = new PlantLineSegment(line.Line.Id, line.StateStart, now, line.State, line.Current?.Sku ?? line.Sku, line.Current?.Id,
                line.Reason, line.Unit, running ? line.Rate : 0, running ? line.Reject : 0);
            segments.Add(segment);
            // The same arithmetic, in the same order, as PlantSignals' cumulative
            // counts, so a resumed run's base totals are bit-identical.
            double gross = segment.CasesPerMinute * (segment.End - segment.Start).TotalMinutes;
            line.GoodTotal += gross * (1 - segment.RejectPercent / 100);
            line.RejectTotal += gross * segment.RejectPercent / 100;
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
                At(now.AddMinutes(line.Line.ChangeoverMinutes), FinishPriority, new Event(Kind.ChangeoverEnd, Line: line.Line.Id, Version: line.Version));
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
            At(now.AddMinutes(minutes), stop == "complete" ? FinishPriority : FailurePriority,
                new Event(Kind.RunStop, Line: line.Line.Id, Version: line.Version, Id: stop, Ref: failing?.Code));
        }

        private void RunStop(LineRuntime line, int version, string stop, string? failingCode)
        {
            if (line.Version != version) return;
            LeaveRunning(line);
            switch (stop)
            {
                case "complete": CompleteOrder(line); break;
                case "failure":
                    var failing = Mode(failingCode!);
                    line.HoursToFailure[failing.Code] = failureDraws.Exponential(failing.MtbfRunHours);
                    Fail(line, failing, planted: false);
                    break;
                case "minor": MinorStop(line); break;
                default: Fail(line, Mode(line.PlantedFailures.Dequeue()), planted: true); break;
            }
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
            // Without a Shipping area an order is done when produced; with one, when shipped.
            if (model.Shipping is null)
                Record("salesorders", order.SalesOrder, "update", new() { ["status"] = "Completed", ["completed_utc"] = now, ["on_time"] = now <= order.Due });
            else
                Record("salesorders", order.SalesOrder, "update", new() { ["status"] = "Produced" });
            if (model.Quality is not null || model.Shipping is not null) CreateLot(line, order, good);
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
            At(now.AddMinutes(minutes), FinishPriority, new Event(Kind.MinorStopEnd, Line: line.Line.Id, Version: line.Version));
        }

        private double NextMinorStopHours() => model.Maintenance.MinorStops.PerRunHour > 0
            ? minorDraws.Exponential(1 / model.Maintenance.MinorStops.PerRunHour) : double.PositiveInfinity;

        // ---------------- quality and shipping ----------------

        /// <summary>
        /// A finished order becomes a lot. With a Quality area it waits in
        /// quarantine for its lab results; otherwise it is released at once.
        /// </summary>
        private void CreateLot(LineRuntime line, ProductionOrder order, long cases)
        {
            string lot = NextId("LOT");
            Record("lots", lot, "insert", new()
            {
                ["lot_id"] = lot, ["production_order"] = order.Id, ["sales_order"] = order.SalesOrder, ["sku"] = order.Sku,
                ["line_id"] = line.Line.Id, ["cases"] = cases, ["produced_utc"] = now,
                ["status"] = model.Quality is null ? "Released" : "Quarantine", ["released_utc"] = model.Quality is null ? now : null, ["shipped_utc"] = null
            });
            if (model.Quality is not { } quality)
            {
                Release(lot, order.Ref(), cases);
                return;
            }
            string sample = NextId("QS");
            Record("qualitysamples", sample, "insert", new() { ["sample_id"] = sample, ["lot_id"] = lot, ["line_id"] = line.Line.Id, ["sku"] = order.Sku, ["sampled_utc"] = now, ["result_utc"] = null, ["status"] = "Pending", ["passed"] = null });
            var random = new PlantRandom(model.Seed, "lab:" + sample);
            At(now.AddMinutes(random.Uniform(quality.LabMinutes)), StatePriority,
                new Event(Kind.LabResult, Line: line.Line.Id, Id: sample, Ref: lot, Number: cases, Time: now, Order: order.Ref()));
        }

        private void LabResult(string sample, string lot, string lineId, OrderRef order, DateTimeOffset produced, long cases)
        {
            var quality = model.Quality!;
            // The sample's own stream: its first draw set the lab delay, its second sets any hold time.
            var random = new PlantRandom(model.Seed, "lab:" + sample);
            random.Uniform(quality.LabMinutes);
            var failed = new List<string>();
            foreach (var parameter in quality.Parameters)
            {
                // The lab measures the product as it was made: the analyzer value at the sample time.
                double value = PlantQualityValues.Value(model, parameter, lineId, order.Sku, produced);
                bool ok = PlantQualityValues.InSpec(parameter, value);
                if (!ok) failed.Add(FormattableString.Invariant($"{parameter.Name} {value} {(value > parameter.Upper ? "above" : "below")} {(value > parameter.Upper ? parameter.Upper : parameter.Lower)}"));
                string result = NextId("QR");
                Record("qualityresults", result, "insert", new() { ["result_id"] = result, ["sample_id"] = sample, ["parameter"] = parameter.Name, ["value"] = value, ["unit"] = parameter.Unit, ["lower_limit"] = parameter.Lower, ["upper_limit"] = parameter.Upper, ["in_spec"] = ok, ["result_utc"] = now });
            }
            Record("qualitysamples", sample, "update", new() { ["result_utc"] = now, ["status"] = "Complete", ["passed"] = failed.Count == 0 });
            if (failed.Count == 0)
            {
                Record("lots", lot, "update", new() { ["status"] = "Released", ["released_utc"] = now });
                Release(lot, order, cases);
                return;
            }
            string hold = NextId("QH");
            Record("qualityholds", hold, "insert", new() { ["hold_id"] = hold, ["lot_id"] = lot, ["sample_id"] = sample, ["reason"] = string.Join("; ", failed), ["created_utc"] = now, ["released_utc"] = null, ["disposition"] = null });
            Record("lots", lot, "update", new() { ["status"] = "On Hold" });
            At(now.AddMinutes(random.Uniform(quality.HoldMinutes)), StatePriority, new Event(Kind.HoldRelease, Id: hold, Ref: lot, Number: cases, Order: order));
        }

        /// <summary>A released lot goes into stock and ships on the next truck for its sales order.</summary>
        private void Release(string lot, OrderRef order, long cases)
        {
            if (model.Shipping is not { } shipping) return;
            string receipt = NextId("INV");
            Record("inventorytransactions", receipt, "insert", new() { ["transaction_id"] = receipt, ["lot_id"] = lot, ["sku"] = order.Sku, ["transaction_type"] = "Receipt", ["quantity_cases"] = cases, ["transaction_utc"] = now });
            var random = new PlantRandom(model.Seed, "shipment:" + lot);
            string shipment = NextId("SH");
            var planned = now.AddHours(random.Uniform(shipping.DelayHours));
            string carrier = shipping.Carriers[random.Integer(0, shipping.Carriers.Count - 1)];
            Record("shipments", shipment, "insert", new() { ["shipment_id"] = shipment, ["sales_order"] = order.SalesOrder, ["lot_id"] = lot, ["carrier"] = carrier, ["quantity_cases"] = cases, ["planned_utc"] = planned, ["shipped_utc"] = null, ["status"] = "Scheduled" });
            At(planned, StatePriority, new Event(Kind.Ship, Id: shipment, Ref: lot, Number: cases, Order: order));
        }

        private void Ship(string shipment, string lot, OrderRef order, long cases)
        {
            Record("shipments", shipment, "update", new() { ["shipped_utc"] = now, ["status"] = "Shipped" });
            string issue = NextId("INV");
            Record("inventorytransactions", issue, "insert", new() { ["transaction_id"] = issue, ["lot_id"] = lot, ["sku"] = order.Sku, ["transaction_type"] = "Shipment", ["quantity_cases"] = -cases, ["transaction_utc"] = now });
            Record("lots", lot, "update", new() { ["status"] = "Shipped", ["shipped_utc"] = now });
            Record("salesorders", order.SalesOrder, "update", new() { ["status"] = "Shipped", ["completed_utc"] = now, ["on_time"] = now <= order.Due });
        }

        // ---------------- breakdowns: signals → records ----------------

        private void PlantedIncident(PlantIncident incident)
        {
            var mode = Mode(incident.FailureMode);
            var line = lines[mode.Line];
            if (line.State == "Running")
            {
                LeaveRunning(line);
                Fail(line, mode, planted: true);
            }
            else line.PlantedFailures.Enqueue(mode.Code); // fails shortly after the line next runs
        }

        private void Fail(LineRuntime line, PlantFailureMode mode, bool planted)
        {
            long number = failureCount++;
            failures.Add((number, new(line.Line.Id, mode.Equipment, mode.Code, now, null, mode.WarningHours, planted)));
            SetState(line, "Down", mode.Description, mode.Equipment);
            // Output is now zero. The detector rule is "line speed zero for
            // DetectionMinutes while work is scheduled"; the line stays Down until
            // repaired, so the rule fires exactly DetectionMinutes after the failure.
            At(now.AddMinutes(model.Maintenance.DetectionMinutes), FailurePriority,
                new Event(Kind.Detect, Line: line.Line.Id, Ref: mode.Code, Time: now, Index: (int)number));
        }

        private void Detect(LineRuntime line, PlantFailureMode mode, DateTimeOffset failedAt, int failureNumber)
        {
            string downtime = NextId("DT");
            Record("downtimeevents", downtime, "insert", Downtime(downtime, line.Line.Id, line.Line.Id + "." + mode.Equipment, "Unplanned", mode.Description, mode.Code, failedAt, now, null));
            var ticket = new Ticket
            {
                Id = NextId("MT"), Type = "Corrective", Line = line.Line.Id, Equipment = mode.Equipment, Code = mode.Code,
                Skill = mode.Skill, Priority = mode.Priority, Created = now, Downtime = downtime, DowntimeStart = failedAt,
                ModeCode = mode.Code, FailureNumber = failureNumber, Ready = true
            };
            OpenTicket(ticket, $"Detected: {mode.Description}");
            TryAssign();
        }

        // ---------------- maintenance ----------------

        private void PreventiveDue(int index)
        {
            var pm = model.Maintenance.Preventive[index];
            var line = lines[pm.Line];
            if (line.PendingPreventive is not null) return; // the previous one has not run yet; do not stack
            var ticket = new Ticket
            {
                Id = NextId("MT"), Type = "Preventive", Line = pm.Line, Equipment = pm.Equipment, Code = pm.Code,
                Skill = pm.Skill, Priority = "Low", Created = now, PreventiveIndex = index
            };
            OpenTicket(ticket, "Scheduled preventive maintenance");
            line.PendingPreventive = ticket;
            if (line.State == "Idle") Dispatch(line); // otherwise it starts when the current order finishes
        }

        private void BeginPreventive(LineRuntime line, Ticket ticket)
        {
            SetState(line, "Maintenance", model.Maintenance.Preventive[ticket.PreventiveIndex].Description, ticket.Equipment);
            ticket.Downtime = NextId("DT");
            ticket.DowntimeStart = now;
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
                double work = ticket.ModeCode is { } code ? random.Uniform(Mode(code).RepairMinutes) : model.Maintenance.Preventive[ticket.PreventiveIndex].DurationMinutes;
                At(now.AddMinutes(response), StatePriority, new Event(Kind.TechnicianArrives, Id: ticket.Id, Ref: technician.Person.Id, Number: work));
            }
        }

        private void CompleteTicket(Ticket ticket, TechnicianRuntime technician)
        {
            Record("maintenancetickets", ticket.Id, "update", new() { ["status"] = "Complete", ["completed_utc"] = now });
            StatusHistory(ticket, "Complete", ticket.Type == "Corrective" ? "Repaired; line returned to service" : "Preventive maintenance done");
            foreach (var use in ticket.ModeCode is { } code ? Mode(code).Parts : [])
            {
                string key = NextId("PU");
                Record("partsusage", key, "insert", new() { ["usage_id"] = key, ["ticket_id"] = ticket.Id, ["part_id"] = use.Part, ["quantity"] = use.Quantity, ["used_utc"] = now });
            }
            technician.Busy = false;
            technician.Completed++;
            openTickets.Remove(ticket);
            var line = lines[ticket.Line];
            Record("downtimeevents", ticket.Downtime!, "update", new() { ["end_utc"] = now, ["duration_minutes"] = Math.Round((now - ticket.DowntimeStart!.Value).TotalMinutes, 1) });
            if (ticket.Type == "Corrective")
            {
                int slot = failures.FindIndex(f => f.Number == ticket.FailureNumber);
                failures[slot] = (ticket.FailureNumber, failures[slot].Failure with { RepairedUtc = now });
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

/// <summary>A stored snapshot that this engine cannot resume from. The caller replays from the plant start instead.</summary>
public sealed class PlantSnapshotException(string message) : Exception(message);
