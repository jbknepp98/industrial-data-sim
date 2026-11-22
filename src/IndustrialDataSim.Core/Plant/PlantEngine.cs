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
    /// <remarks>
    /// Version 2: the snapshot holds certificate state (maintenance.certification).
    /// Version 3: the snapshot holds batch state (the running batch and phase, released
    /// intermediate stock and incoming batch quantity).
    /// Version 4: golden batch drift and batch quality. Batches carry drift effects,
    /// yield and deviation; lines carry the process temperature (repairs reheat
    /// gradually) and the BatchDeviation value; the snapshot holds drift ends,
    /// batches waiting for release, and lots waiting for their batches.
    /// </remarks>
    public const int EngineVersion = 4;

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
    // Certificate issues and expiries come last, so plants without them keep
    // exactly their original order.
    private const int DemandGroup = 0, ShiftGroup = 1, PreventiveGroup = 2, IncidentGroup = 3, DispatchGroup = 4, CertificationGroup = 5;

    private static long CalendarOrder(int group, long major, long minor) =>
        long.MinValue + ((long)group << 58) + (major << 20) + minor;

    private enum Kind
    {
        Dispatch, Incident, Order, ShiftStart, PreventiveDue, ChangeoverEnd, RunStop, MinorStopEnd,
        LabResult, HoldRelease, Ship, Detect, TechnicianArrives, TicketComplete,
        CertificateIssue, CertificateExpiry, SignoffEnd, BatchLabResult, BatchHoldRelease, DeviationClose
    }

    /// <summary>A pending event as data. Which fields are used depends on Kind.</summary>
    private sealed record Event(Kind Kind, string? Line = null, int Version = 0, int Index = 0, string? Id = null,
        string? Ref = null, string? Ref2 = null, double Number = 0, DateTimeOffset? Time = null, DateTimeOffset? Time2 = null,
        OrderRef? Order = null);

    /// <summary>What a lot, lab result or shipment needs to know about its finished production order.</summary>
    /// <remarks>QualityShift: what a fed order inherited from the batches it consumed (null when nothing).</remarks>
    private sealed record OrderRef(string Id, string SalesOrder, string Sku, DateTimeOffset Due, Dictionary<string, double>? QualityShift = null);

    private sealed class ProductionOrder
    {
        public required string Id { get; init; }
        public string? SalesOrder { get; init; }            // null for a batch order that replenishes a fed line's intermediate
        public required string Sku { get; init; }
        public required string Line { get; init; }
        public required double GoodTarget { get; init; }
        public required DateTimeOffset Created { get; init; }
        public required DateTimeOffset Due { get; init; }
        public double Good { get; set; }
        public double Rejected { get; set; }
        public DateTimeOffset? Started { get; set; }
        public string? Run { get; set; }
        public string? Operator { get; set; }        // with operator certification: the operator checked at run start
        public bool Checked { get; set; }            // the operator certification check has been made
        public bool Uncertified { get; set; }        // ran anyway without a current certificate
        public Dictionary<string, double>? QualityShift { get; set; } // fed orders: quality inherited from the consumed batches

        public OrderRef Ref() => new(Id, SalesOrder!, Sku, Due, QualityShift);
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
        public BatchState? CurrentBatch;           // batch lines: the batch in progress
        public PlantBatchSegment? SegmentBatch;    // batch lines: the batch phase of the open segment, from its start
        public double TemperatureC = PlantBatchThermal.AmbientC; // batch lines: process temperature at the last segment boundary (no noise)
        public double Deviation;                   // batch lines: the BatchDeviation value, updated at the end of each phase
        public double? SegmentDeviation;           // batch lines: Deviation as of the open segment's start
        public Dictionary<string, double>? SegmentShift; // the open segment's order quality shift, from its start
    }

    /// <summary>
    /// The batch in progress on a batch line. Planned holds every phase's drawn
    /// duration (whole seconds, from the batch's own random stream); Remaining is
    /// the current phase's running time still to go, as of the last time the line
    /// started running. A failure stops the clock; the repair resumes it.
    /// </summary>
    private sealed class BatchState
    {
        public required string Id { get; init; }
        public required string Recipe { get; init; }
        public required double Size { get; init; }
        public required int[] Planned { get; init; }
        public int Phase { get; set; }
        public DateTimeOffset PhaseStart { get; set; }
        public long Remaining { get; set; }
        public bool Interrupted { get; set; }
        public double[] Lag { get; init; } = [];   // per phase: how far the temperature trails the setpoint at mid-phase (°C)
        public double Yield { get; init; }         // percent, drawn at the start
        public double Deviation { get; set; }      // the worst phase score so far
    }

    /// <summary>A released batch of an intermediate in stock for a fed line, with the quantity not yet consumed.</summary>
    private sealed class StockBatch
    {
        public required string Id { get; init; }
        public double Remaining { get; set; }
        public Dictionary<string, double>? Shift { get; init; } // quality the batch passes on to what is made from it
    }

    /// <summary>A discharged batch in quarantine, waiting for its lab result and, when it has one, its deviation's closure.</summary>
    private sealed class PendingBatch
    {
        public required string Id { get; init; }
        public required string Order { get; init; }
        public required string Line { get; init; }
        public required string Sku { get; init; }
        public required string Recipe { get; init; }
        public double Size { get; init; }
        public double Yield { get; init; }
        public bool Intermediate { get; init; }
        public Dictionary<string, double>? Shift { get; init; }
        public bool LabPending { get; set; }
        public bool DeviationOpen { get; set; }
    }

    /// <summary>A finished batch-unit lot waiting for its last batches to be released.</summary>
    private sealed record WaitingLot(string Lot, OrderRef Order, long Cases);

    private sealed class TechnicianRuntime(PlantPerson person)
    {
        public readonly PlantPerson Person = person;
        public bool Busy;
        public int Completed;
    }

    // ---------------- snapshot layout (EngineVersion 4) ----------------

    private sealed record StateDto(
        long EventOrder, long ChangeSequence, long FailureCount, double Stock, DateTimeOffset CalendarUntil,
        DateTime[] PreventiveNextDue, int[] PreventiveDueIndex, ulong FailureDraws, ulong MinorDraws,
        Dictionary<string, int> Counters, List<LineDto> Lines, List<TechnicianDto> Technicians,
        List<Ticket> OpenTickets, List<FailureDto> Failures, List<EventDto> Events,
        Dictionary<string, CertificateDto>? Certificates = null, // null for plants without certification
        Dictionary<string, List<StockBatch>>? IntermediateStock = null, // null for plants without batch.feeds
        Dictionary<string, double>? IntermediateIncoming = null,
        Dictionary<int, DateTimeOffset>? DriftEnded = null,      // null for plants without drifts
        List<PendingBatch>? PendingBatches = null,              // null unless batches wait for release (quality.batchRelease)
        Dictionary<string, int>? Unreleased = null,
        Dictionary<string, WaitingLot>? WaitingLots = null);

    private sealed record LineDto(string Id, string State, DateTimeOffset StateStart, string? Sku, string? Reason, string? Unit,
        ProductionOrder? Current, List<ProductionOrder> Queue, int Version, DateTimeOffset RunningSince, double Rate, double Reject,
        Dictionary<string, double> HoursToFailure, double HoursToMinorStop, List<string> PlantedFailures,
        string? PendingPreventive, string? ChangeoverDowntime, double GoodTotal, double RejectTotal,
        BatchState? CurrentBatch = null, PlantBatchSegment? SegmentBatch = null,
        double TemperatureC = PlantBatchThermal.AmbientC, double Deviation = 0, double? SegmentDeviation = null, Dictionary<string, double>? SegmentShift = null);

    private sealed record TechnicianDto(string Id, bool Busy, int Completed);

    private sealed record FailureDto(long Number, PlantFailure Failure);

    /// <summary>The current certificate of one employee and skill (or work instruction): its training record and validity.</summary>
    private sealed record CertificateDto(string Record, DateTimeOffset Issued, DateTimeOffset Expires);

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
        private readonly PlantCertification? certification;
        private readonly List<(PlantCertificateIssue Issue, int ValidDays)> certificateIssues = [];
        private Dictionary<string, CertificateDto> certificates = new(StringComparer.Ordinal);
        // Batch units feeding packaging lines: released intermediate stock (first in, first
        // out) and the quantity still to come from released batch orders, per intermediate SKU.
        private readonly bool feeds;
        private Dictionary<string, List<StockBatch>> intermediateStock = new(StringComparer.Ordinal);
        private Dictionary<string, double> intermediateIncoming = new(StringComparer.Ordinal);
        private readonly IReadOnlyList<PlantSku> orderable;
        // Golden batch: when each drift ended early (its cleaning PM completed), batches
        // in quarantine, unreleased batches per direct-sale order, and lots waiting for them.
        private Dictionary<int, DateTimeOffset> driftEnded = new();
        private readonly bool gated, batchPlant;
        private Dictionary<string, PendingBatch> pendingBatches = new(StringComparer.Ordinal);
        private Dictionary<string, int> unreleased = new(StringComparer.Ordinal);
        private Dictionary<string, WaitingLot> waitingLots = new(StringComparer.Ordinal);

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
            certification = model.Maintenance.Certification;
            if (certification is not null)
            {
                // Every certificate issue (first issues, renewals, work instructions) in time order;
                // the index orders their calendar events.
                var all = certification.Issued.Concat(certification.Renewals).Select(i => (i, certification.ValidDays))
                    .Concat((certification.Operators?.WorkInstructions ?? []).SelectMany(w => w.Issued.Select(i => (i, w.ValidDays))));
                certificateIssues = all.OrderBy(i => i.i.IssuedUtc).ToList();
            }
            foreach (var use in model.Lines.SelectMany(l => l.Batch?.Feeds?.Consumption ?? []))
            {
                feeds = true;
                intermediateStock.TryAdd(use.Intermediate, []);
                intermediateIncoming.TryAdd(use.Intermediate, 0);
            }
            // Intermediates are consumed by a fed line, never ordered by customers.
            var intermediates = model.Lines.SelectMany(l => l.Batch?.Feeds?.Consumption ?? []).Select(c => c.Intermediate).ToHashSet(StringComparer.Ordinal);
            orderable = intermediates.Count == 0 ? model.Skus : model.Skus.Where(s => !intermediates.Contains(s.Code)).ToList();
            gated = model.Quality?.GatesBatches == true;
            batchPlant = model.Lines.Any(l => l.Batch is not null);
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
                state.PreventiveNextDue.Length != model.Maintenance.Preventive.Count || state.Technicians.Count != model.Technicians.Count ||
                (state.Certificates is null) != (model.Maintenance.Certification is null) ||
                (state.IntermediateStock is null) != model.Lines.All(l => l.Batch?.Feeds is null) ||
                state.Lines.Any(l => l.CurrentBatch is not null && model.Lines.FirstOrDefault(m => m.Id == l.Id)?.Batch is null) ||
                (state.DriftEnded is null) != (model.Drifts.Count == 0) || (state.PendingBatches is null) != (model.Quality?.GatesBatches != true))
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
                line.CurrentBatch = dto.CurrentBatch;
                line.SegmentBatch = dto.SegmentBatch;
                line.TemperatureC = dto.TemperatureC;
                line.Deviation = dto.Deviation;
                line.SegmentDeviation = dto.SegmentDeviation;
                line.SegmentShift = dto.SegmentShift;
            }
            foreach (var (technician, dto) in technicians.Zip(state.Technicians))
            {
                if (technician.Person.Id != dto.Id) throw new PlantSnapshotException("The snapshot's technicians do not match this plant model.");
                technician.Busy = dto.Busy;
                technician.Completed = dto.Completed;
            }
            if (state.Certificates is not null) certificates = new Dictionary<string, CertificateDto>(state.Certificates, StringComparer.Ordinal);
            if (state.IntermediateStock is not null)
            {
                intermediateStock = new Dictionary<string, List<StockBatch>>(state.IntermediateStock, StringComparer.Ordinal);
                intermediateIncoming = new Dictionary<string, double>(state.IntermediateIncoming!, StringComparer.Ordinal);
            }
            if (state.DriftEnded is not null) driftEnded = new Dictionary<int, DateTimeOffset>(state.DriftEnded);
            if (state.PendingBatches is not null)
            {
                pendingBatches = state.PendingBatches.ToDictionary(b => b.Id, StringComparer.Ordinal);
                unreleased = new Dictionary<string, int>(state.Unreleased!, StringComparer.Ordinal);
                waitingLots = new Dictionary<string, WaitingLot>(state.WaitingLots!, StringComparer.Ordinal);
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
                    l.PendingPreventive?.Id, l.ChangeoverDowntime, l.GoodTotal, l.RejectTotal, l.CurrentBatch, l.SegmentBatch,
                    l.TemperatureC, l.Deviation, l.SegmentDeviation, l.SegmentShift)).ToList(),
                technicians.Select(t => new TechnicianDto(t.Person.Id, t.Busy, t.Completed)).ToList(),
                openTickets,
                // Failures repaired before the snapshot no longer affect any signal
                // or record at or after it; open ones are kept for tickets and vibration.
                failures.Where(f => f.Failure.RepairedUtc is null || f.Failure.RepairedUtc >= at)
                    .Select(f => new FailureDto(f.Number, f.Failure)).ToList(),
                events.UnorderedItems.Select(e => new EventDto(e.Priority.Ticks, e.Priority.Priority, e.Priority.Order, e.Element))
                    .OrderBy(e => e.Ticks).ThenBy(e => e.Priority).ThenBy(e => e.Order).ToList(),
                certification is null ? null : new Dictionary<string, CertificateDto>(certificates, StringComparer.Ordinal),
                feeds ? intermediateStock.ToDictionary(p => p.Key, p => p.Value.Select(b => new StockBatch { Id = b.Id, Remaining = b.Remaining, Shift = b.Shift }).ToList(), StringComparer.Ordinal) : null,
                feeds ? new Dictionary<string, double>(intermediateIncoming, StringComparer.Ordinal) : null,
                model.Drifts.Count == 0 ? null : new Dictionary<int, DateTimeOffset>(driftEnded),
                gated ? pendingBatches.Values.OrderBy(b => b.Id, StringComparer.Ordinal).ToList() : null,
                gated ? new Dictionary<string, int>(unreleased, StringComparer.Ordinal) : null,
                gated ? new Dictionary<string, WaitingLot>(waitingLots, StringComparer.Ordinal) : null);
            return new PlantSnapshot(EngineVersion, at, changeSequence, JsonSerializer.Serialize(state, SnapshotJson));
        }

        public PlantRunResult Execute(DateTimeOffset? snapshotAt)
        {
            if (fresh)
            {
                WriteReferenceData();
                if (certification is not null) StartCertificates();
                foreach (var runtime in lines.Values)
                {
                    // Draw each failure mode's first time-to-failure in declaration order.
                    foreach (var mode in model.Maintenance.FailureModes.Where(m => m.Line == runtime.Line.Id))
                        runtime.HoursToFailure[mode.Code] = failureDraws.Exponential(mode.MtbfRunHours);
                    // Minor stops are a packaging-line behaviour; a batch unit draws none.
                    runtime.HoursToMinorStop = runtime.Line.Batch is null ? NextMinorStopHours() : double.PositiveInfinity;
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
                case Kind.CertificateIssue: IssueCertificate(e.Index); break;
                case Kind.CertificateExpiry: ExpireCertificate(e.Index, e.Id!); break;
                case Kind.SignoffEnd:
                {
                    var line = lines[e.Line!];
                    if (line.Version == e.Version) StartRunning(line);
                    break;
                }
                case Kind.BatchLabResult: BatchLabResult(e.Id!, e.Ref!); break;
                case Kind.BatchHoldRelease:
                {
                    Record("qualityholds", e.Id!, "update", new() { ["released_utc"] = now, ["disposition"] = "Released after investigation and retest" });
                    var batch = pendingBatches[e.Ref!];
                    batch.LabPending = false;
                    if (batch.DeviationOpen) Record("batches", batch.Id, "update", new() { ["status"] = "Quarantine" });
                    TryReleaseBatch(batch);
                    break;
                }
                case Kind.DeviationClose: CloseDeviation(e.Id!, e.Ref!); break;
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
            foreach (var person in model.QualityStaff)
                Record("employees", person.Id, "insert", new() { ["employee_id"] = person.Id, ["employee_name"] = person.Name, ["role"] = "QA", ["shift_name"] = person.Shift, ["line_id"] = null });
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
            foreach (var instruction in certification?.Operators?.WorkInstructions ?? [])
                Record("workinstructions", instruction.Id, "insert", new() { ["instruction_id"] = instruction.Id, ["line_id"] = instruction.Line, ["skus"] = string.Join(",", instruction.Skus), ["valid_days"] = instruction.ValidDays });
            foreach (var recipe in model.Lines.SelectMany(l => l.Batch?.Recipes ?? []))
            {
                Record("recipes", recipe.Id, "insert", new() { ["recipe_id"] = recipe.Id, ["sku"] = recipe.Sku, ["phase_count"] = recipe.Phases.Count });
                for (int i = 0; i < recipe.Phases.Count; i++)
                {
                    var phase = recipe.Phases[i];
                    string key = FormattableString.Invariant($"{recipe.Id}-{i + 1:00}");
                    string setpoints = JsonSerializer.Serialize(new
                    {
                        temperatureC = new { start = phase.TemperatureC.Start, end = phase.TemperatureC.End },
                        pressureBar = new { start = phase.PressureBar.Start, end = phase.PressureBar.End },
                        agitatorRpm = new { start = phase.AgitatorRpm.Start, end = phase.AgitatorRpm.End },
                        levelPercent = new { start = phase.LevelPercent.Start, end = phase.LevelPercent.End }
                    });
                    Record("recipephases", key, "insert", new() { ["recipe_phase_id"] = key, ["recipe_id"] = recipe.Id, ["sequence"] = i + 1, ["phase"] = phase.Name, ["minutes_min"] = phase.Minutes.Minimum, ["minutes_max"] = phase.Minutes.Maximum, ["setpoints_json"] = setpoints });
                    Record("goldenprofiles", key, "insert", new() { ["profile_id"] = key, ["recipe_id"] = recipe.Id, ["sequence"] = i + 1, ["phase"] = phase.Name, ["golden_minutes"] = recipe.Golden.PhaseMinutes[i], ["setpoints_json"] = setpoints, ["golden_yield_percent"] = recipe.Golden.YieldTarget });
                }
            }
        }

        // ---------------- certificates ----------------

        private static string CertificateKey(string employee, string skill) => employee + "|" + skill;

        /// <summary>
        /// At the plant start, each employee and skill holds the certificate from
        /// its latest issue so far (recorded with its true issue time). Later
        /// issues and every expiry are calendar events at their own times.
        /// </summary>
        private void StartCertificates()
        {
            var latest = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < certificateIssues.Count; i++)
            {
                var issue = certificateIssues[i].Issue;
                if (issue.IssuedUtc <= model.StartUtc) latest[CertificateKey(issue.Employee, issue.Skill)] = i;
                else Calendar(issue.IssuedUtc, FinishPriority, CalendarOrder(CertificationGroup, 0, i), new Event(Kind.CertificateIssue, Index: i));
            }
            foreach (var index in latest.Values.Order()) IssueCertificate(index);
        }

        private void IssueCertificate(int index)
        {
            var (issue, validDays) = certificateIssues[index];
            string key = CertificateKey(issue.Employee, issue.Skill);
            var expires = issue.IssuedUtc.AddDays(validDays);
            if (certificates.TryGetValue(key, out var previous) && previous.Expires > now)
                Record("trainingrecords", previous.Record, "update", new() { ["status"] = "Renewed" });
            string record = NextId("TR");
            Record("trainingrecords", record, "insert", new()
            {
                ["record_id"] = record, ["employee_id"] = issue.Employee, ["skill"] = issue.Skill,
                ["issued_utc"] = issue.IssuedUtc, ["expires_utc"] = expires, ["status"] = expires > now ? "Current" : "Expired"
            });
            certificates[key] = new CertificateDto(record, issue.IssuedUtc, expires);
            if (expires > now)
                Calendar(expires, FinishPriority, CalendarOrder(CertificationGroup, 1, index), new Event(Kind.CertificateExpiry, Index: index, Id: record));
            if (now > model.StartUtc) TryAssign(); // a renewed technician may take a waiting ticket
        }

        private void ExpireCertificate(int index, string record)
        {
            var issue = certificateIssues[index].Issue;
            if (certificates.TryGetValue(CertificateKey(issue.Employee, issue.Skill), out var current) && current.Record == record)
                Record("trainingrecords", record, "update", new() { ["status"] = "Expired" });
        }

        private bool Certified(string employee, string skill) =>
            certificates.TryGetValue(CertificateKey(employee, skill), out var c) && c.Issued <= now && now < c.Expires;

        private string CertificateReason(string employee, string skill) =>
            certificates.TryGetValue(CertificateKey(employee, skill), out var c)
                ? now < c.Expires
                    ? FormattableString.Invariant($"certificate valid until {c.Expires:yyyy-MM-dd}")
                    : FormattableString.Invariant($"certificate expired {c.Expires:yyyy-MM-dd}")
                : $"no certificate for {skill}";

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
                var sku = orderable[random.Integer(0, orderable.Count - 1)];
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
            var order = new ProductionOrder { Id = productionOrder, SalesOrder = salesOrder, Sku = sku.Code, Line = line.Line.Id, GoodTarget = cases, Created = created, Due = due };
            if (feeds) Replenish(line, order);
            line.Queue.Add(order);
            if (line.State == "Idle") Dispatch(line);
        }

        // ---------------- batch units feeding a packaging line ----------------

        /// <summary>The batch unit feeding this line's SKU and what one case uses, or null when the SKU is not fed.</summary>
        private (LineRuntime Feeder, PlantConsumption Use)? FedBy(string lineId, string sku)
        {
            foreach (var runtime in lines.Values)
                if (runtime.Line.Batch?.Feeds is { } f && f.Line == lineId && f.Consumption.FirstOrDefault(c => c.Sku == sku) is { } use)
                    return (runtime, use);
            return null;
        }

        private static double Need(ProductionOrder order, PlantConsumption use) => Math.Round(order.GoodTarget * use.PerCase, 3);

        private double OnHand(string intermediate) => intermediateStock[intermediate].Sum(b => b.Remaining);

        /// <summary>
        /// A new packaging order for a fed SKU: when released stock and the batches
        /// already on order cannot cover it and the orders waiting before it, the
        /// batch unit gets a batch order (no sales order) for whole batches.
        /// </summary>
        private void Replenish(LineRuntime line, ProductionOrder order)
        {
            if (FedBy(line.Line.Id, order.Sku) is not { } fed) return;
            var (feeder, use) = fed;
            double waiting = line.Queue.Sum(o => FedBy(line.Line.Id, o.Sku) is { } other && other.Use.Intermediate == use.Intermediate ? Need(o, other.Use) : 0);
            double shortfall = waiting + Need(order, use) - OnHand(use.Intermediate) - intermediateIncoming[use.Intermediate];
            if (shortfall <= 1e-6) return;
            var unit = feeder.Line.Batch!;
            int batches = (int)Math.Ceiling(shortfall / unit.Capacity - 1e-9);
            double quantity = batches * unit.Capacity;
            string id = NextId("PO");
            Record("productionorders", id, "insert", new() { ["production_order"] = id, ["sales_order"] = null, ["sku"] = use.Intermediate, ["line_id"] = feeder.Line.Id, ["quantity_cases"] = quantity, ["released_utc"] = now, ["due_utc"] = order.Due, ["status"] = "Released", ["started_utc"] = null, ["completed_utc"] = null, ["good_cases"] = null, ["reject_cases"] = null });
            feeder.Queue.Add(new ProductionOrder { Id = id, SalesOrder = null, Sku = use.Intermediate, Line = feeder.Line.Id, GoodTarget = quantity, Created = now, Due = order.Due });
            intermediateIncoming[use.Intermediate] += quantity;
            if (feeder.State == "Idle") Dispatch(feeder);
        }

        /// <summary>Takes a fed order's intermediate from released batches, oldest first, and records the genealogy.</summary>
        private void Consume(ProductionOrder order, PlantConsumption use)
        {
            double need = Need(order, use);
            var stock = intermediateStock[use.Intermediate];
            Dictionary<string, double>? shift = null;
            double total = 0;
            while (need > 1e-6 && stock.Count > 0)
            {
                var batch = stock[0];
                double quantity = Math.Round(Math.Min(batch.Remaining, need), 3);
                // The product inherits each batch's quality in proportion to what it took.
                total += quantity;
                foreach (var (parameter, value) in batch.Shift ?? [])
                {
                    shift ??= new Dictionary<string, double>(StringComparer.Ordinal);
                    shift[parameter] = shift.GetValueOrDefault(parameter) + value * quantity;
                }
                string id = NextId("BC");
                Record("batchconsumption", id, "insert", new() { ["consumption_id"] = id, ["batch_id"] = batch.Id, ["production_order"] = order.Id, ["quantity"] = quantity, ["consumed_utc"] = now });
                batch.Remaining = Math.Round(batch.Remaining - quantity, 3);
                need = Math.Round(need - quantity, 3);
                if (batch.Remaining <= 1e-6)
                {
                    stock.RemoveAt(0);
                    Record("batches", batch.Id, "update", new() { ["status"] = "Consumed" });
                }
            }
            if (shift is not null && total > 0)
                order.QualityShift = shift.ToDictionary(p => p.Key, p => Math.Round(p.Value / total, 4), StringComparer.Ordinal);
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
            if (line.CurrentBatch is { } batch)
            {
                var phase = line.Line.Batch!.Recipes.First(r => r.Id == batch.Recipe).Phases[batch.Phase];
                double progress = 1 - (double)batch.Remaining / batch.Planned[batch.Phase];
                line.SegmentBatch = new PlantBatchSegment(batch.Id, batch.Recipe, batch.Phase + 1, phase.Name, batch.Planned[batch.Phase], progress, progress)
                {
                    LagC = batch.Lag.Length > batch.Phase ? batch.Lag[batch.Phase] : 0,
                    StartC = line.TemperatureC
                };
            }
            else line.SegmentBatch = null;
            line.SegmentDeviation = line.Line.Batch is null ? null : line.Deviation;
            line.SegmentShift = line.Current?.QualityShift;
        }

        private void CloseSegment(LineRuntime line)
        {
            if (now <= line.StateStart) return;
            bool running = line.State == "Running";
            var segment = new PlantLineSegment(line.Line.Id, line.StateStart, now, line.State, line.Current?.Sku ?? line.Sku, line.Current?.Id,
                line.Reason, line.Unit, running ? line.Rate : 0, running ? line.Reject : 0)
            {
                // A batch phase advances only while running; a Down interval holds it where it stopped.
                Batch = line.SegmentBatch is { } phase
                    ? phase with { To = running ? Math.Min(1, phase.From + (now - line.StateStart).TotalSeconds / phase.PlannedSeconds) : phase.From }
                    : null,
                Deviation = line.SegmentDeviation,
                QualityShift = line.SegmentShift
            };
            segments.Add(segment);
            if (line.Line.Batch is { } unit)
                // Where the process temperature ended up, so the next segment starts from it (cooling while Down, reheating after).
                line.TemperatureC = segment.Batch is { } b
                    ? PlantBatchThermal.Temperature(unit.Recipes.First(r => r.Id == b.Recipe).Phases[b.Sequence - 1], b, segment.State, (segment.End - segment.Start).TotalMinutes, b.To)
                    : PlantBatchThermal.AmbientC;
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
            if (feeds && !DispatchFed(line, ref next)) return;
            line.Queue.Remove(next);
            line.Current = next;
            if (line.Sku is not null && line.Sku != next.Sku && line.Line.ChangeoverMinutes > 0)
            {
                // A batch unit cleans in place between different products.
                SetState(line, "Changeover", line.Line.Batch is null ? $"Changeover {line.Sku} to {next.Sku}" : $"CIP {line.Sku} to {next.Sku}");
                line.ChangeoverDowntime = NextId("DT");
                Record("downtimeevents", line.ChangeoverDowntime, "insert", Downtime(line.ChangeoverDowntime, line.Line.Id, null, "Planned", "Changeover", null, now, now, null));
                At(now.AddMinutes(line.Line.ChangeoverMinutes), FinishPriority, new Event(Kind.ChangeoverEnd, Line: line.Line.Id, Version: line.Version));
                return;
            }
            StartRunning(line);
        }

        /// <summary>
        /// On a fed line, the earliest-due order whose intermediate is in released
        /// stock goes next and consumes it. With none ready the line waits, Idle.
        /// </summary>
        private bool DispatchFed(LineRuntime line, ref ProductionOrder next)
        {
            ProductionOrder? ready = null;
            string? waitingFor = null;
            foreach (var order in line.Queue.OrderBy(o => o.Due).ThenBy(o => o.Created).ThenBy(o => o.Id, StringComparer.Ordinal))
            {
                if (FedBy(line.Line.Id, order.Sku) is not { } fed) { ready = order; break; }
                if (OnHand(fed.Use.Intermediate) + 1e-6 >= Need(order, fed.Use)) { ready = order; break; }
                waitingFor ??= fed.Use.Intermediate;
            }
            if (ready is null)
            {
                SetState(line, "Idle", $"Waiting for {skus[waitingFor!].Description}");
                return false;
            }
            next = ready;
            if (FedBy(line.Line.Id, ready.Sku) is { } use) Consume(ready, use.Use);
            return true;
        }

        private void StartRunning(LineRuntime line)
        {
            var order = line.Current!;
            var sku = skus[order.Sku];
            line.Sku = order.Sku;
            (string Instruction, bool Certified, string Reason)? compliance = null;
            if (order.Started is null && !order.Checked && certification?.Operators is { } operatorRules)
            {
                // The shift's operator must hold the work instruction for this line and SKU.
                order.Checked = true;
                order.Operator = model.Operators.FirstOrDefault(o => o.Line == line.Line.Id && o.Shift == ShiftAt(now).Name)?.Id;
                var instruction = operatorRules.WorkInstructions.FirstOrDefault(w => w.Line == line.Line.Id && w.Skus.Contains(order.Sku));
                if (instruction is not null && order.Operator is not null)
                {
                    bool certified = Certified(order.Operator, instruction.Id);
                    string reason = CertificateReason(order.Operator, instruction.Id);
                    if (!certified && operatorRules.SupervisorSignoff)
                    {
                        order.Run = NextId("RUN");
                        var signedOff = now.AddMinutes(operatorRules.SignoffMinutes);
                        OperatorCompliance(order, instruction.Id, false, reason, signedOff);
                        SetState(line, "Signoff", $"Awaiting supervisor sign-off ({reason})");
                        At(signedOff, StatePriority, new Event(Kind.SignoffEnd, Line: line.Line.Id, Version: line.Version));
                        return;
                    }
                    order.Uncertified = !certified;
                    compliance = (instruction.Id, certified, reason);
                }
            }
            if (order.Started is null)
            {
                order.Started = now;
                order.Run ??= NextId("RUN");
                string? operatorId = order.Operator ?? model.Operators.FirstOrDefault(o => o.Line == line.Line.Id && o.Shift == ShiftAt(now).Name)?.Id;
                Record("productionruns", order.Run, "insert", new() { ["run_id"] = order.Run, ["production_order"] = order.Id, ["line_id"] = line.Line.Id, ["sku"] = order.Sku, ["operator_id"] = operatorId, ["start_utc"] = now, ["end_utc"] = null, ["good_cases"] = null, ["reject_cases"] = null });
                Record("productionorders", order.Id, "update", new() { ["status"] = "Running", ["started_utc"] = now });
                if (order.SalesOrder is not null) Record("salesorders", order.SalesOrder, "update", new() { ["status"] = "In Production" });
                if (compliance is { } check) OperatorCompliance(order, check.Instruction, check.Certified, check.Reason, null);
            }
            if (line.Line.Batch is { } unit)
            {
                RunBatch(line, unit);
                return;
            }
            line.Rate = sku.CasesPerMinute;
            line.Reject = order.Uncertified && certification!.Operators!.RejectPercentWhenUncertified is { } uplift ? uplift : sku.RejectPercent;
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

        // ---------------- batch units ----------------

        /// <summary>Runs the current batch phase, starting a new batch first when none is in progress.</summary>
        private void RunBatch(LineRuntime line, PlantBatchUnit unit)
        {
            if (line.CurrentBatch is null) BeginBatch(line, unit);
            var batch = line.CurrentBatch!;
            line.Rate = 0;
            line.Reject = 0;
            SetState(line, "Running", unit.Recipes.First(r => r.Id == batch.Recipe).Phases[batch.Phase].Name, null);
            line.RunningSince = now;
            // The next stop is the earliest of: the phase ends, a breakdown, or a planted incident.
            string stop = "phase";
            var when = now.AddSeconds(batch.Remaining);
            PlantFailureMode? failing = null;
            foreach (var mode in model.Maintenance.FailureModes.Where(m => m.Line == line.Line.Id))
            {
                var candidate = now.AddMinutes(line.HoursToFailure[mode.Code] * 60);
                if (candidate < when) { when = candidate; stop = "failure"; failing = mode; }
            }
            if (line.PlantedFailures.Count > 0 && now.AddMinutes(1) < when) { when = now.AddMinutes(1); stop = "planted"; }
            At(when, stop == "phase" ? FinishPriority : FailurePriority, new Event(Kind.RunStop, Line: line.Line.Id, Version: line.Version, Id: stop, Ref: failing?.Code));
        }

        /// <summary>
        /// Starts the order's next batch: at most one capacity, phase durations drawn
        /// from the batch's own stream, so a batch's timing never depends on others.
        /// </summary>
        private void BeginBatch(LineRuntime line, PlantBatchUnit unit)
        {
            var order = line.Current!;
            var recipe = unit.RecipeFor(order.Sku);
            string id = NextId("BATCH");
            var random = new PlantRandom(model.Seed, "batch:" + id);
            var (factors, lags, yieldDelta) = DriftEffects(line.Line.Id, recipe);
            var planned = recipe.Phases.Select((p, i) => Math.Max(60, (int)Math.Round(random.Uniform(p.Minutes) * 60 * factors[i]))).ToArray();
            double yield = Math.Round(Math.Clamp(recipe.Golden.YieldTarget + yieldDelta + recipe.Golden.YieldSigma * random.Gaussian(), 0, 100), 2);
            double size = Math.Round(Math.Min(unit.Capacity, order.GoodTarget - order.Good), 3);
            line.CurrentBatch = new BatchState { Id = id, Recipe = recipe.Id, Size = size, Planned = planned, Phase = 0, PhaseStart = now, Remaining = planned[0], Lag = lags, Yield = yield };
            Record("batches", id, "insert", new() { ["batch_id"] = id, ["production_order"] = order.Id, ["recipe_id"] = recipe.Id, ["line_id"] = line.Line.Id, ["start_utc"] = now, ["end_utc"] = null, ["size"] = size, ["status"] = "Running", ["yield_percent"] = null, ["deviation_score"] = null, ["golden"] = null, ["released_utc"] = null });
            BeginPhaseRecord(line.CurrentBatch, recipe);
        }

        /// <summary>
        /// The drifts acting on a batch of this recipe starting now: per phase, the
        /// running-time factor and temperature lag; and the yield change. Each drift
        /// counts at its current strength (0 before from and after it ended, ramping
        /// linearly to 1 over rampDays).
        /// </summary>
        private (double[] Factors, double[] Lags, double YieldDelta) DriftEffects(string lineId, PlantRecipe recipe)
        {
            var factors = Enumerable.Repeat(1.0, recipe.Phases.Count).ToArray();
            var lags = new double[recipe.Phases.Count];
            double yieldDelta = 0;
            for (int i = 0; i < model.Drifts.Count; i++)
            {
                var drift = model.Drifts[i];
                int phase = recipe.Phases.ToList().FindIndex(p => p.Name == drift.Phase);
                if (drift.Line != lineId || (drift.Recipe is not null && drift.Recipe != recipe.Id) || phase < 0) continue;
                double strength = DriftStrength(i);
                if (strength <= 0) continue;
                factors[phase] *= 1 + (drift.PhaseMinutesFactor - 1) * strength;
                lags[phase] = Math.Round(lags[phase] + drift.TemperatureLagC * strength, 3);
                yieldDelta += drift.YieldDelta * strength;
            }
            return (factors, lags, yieldDelta);
        }

        private double DriftStrength(int index)
        {
            var drift = model.Drifts[index];
            if (now < drift.FromUtc || (drift.UntilUtc is { } until && now >= until) || driftEnded.ContainsKey(index)) return 0;
            return drift.RampDays <= 0 ? 1 : Math.Min(1, (now - drift.FromUtc).TotalDays / drift.RampDays);
        }

        /// <summary>
        /// How far one phase ran from the golden profile: its running minutes away from
        /// the golden minutes, in half-widths of the phase's normal range (so 1 is the
        /// edge of normal), or its temperature lag in units of LagScaleC, whichever is larger.
        /// </summary>
        private static double PhaseScore(PlantRecipe recipe, BatchState batch, int phase)
        {
            var range = recipe.Phases[phase].Minutes;
            double half = Math.Max(0.5, (range.Maximum - range.Minimum) / 2);
            double minutes = Math.Abs(batch.Planned[phase] / 60.0 - recipe.Golden.PhaseMinutes[phase]) / half;
            double lag = batch.Lag.Length > phase ? batch.Lag[phase] / PlantBatchThermal.LagScaleC : 0;
            return Math.Max(minutes, lag);
        }

        private void BeginPhaseRecord(BatchState batch, PlantRecipe recipe)
        {
            string key = FormattableString.Invariant($"{batch.Id}-{batch.Phase + 1:00}");
            Record("batchphases", key, "insert", new() { ["batch_phase_id"] = key, ["batch_id"] = batch.Id, ["sequence"] = batch.Phase + 1, ["phase"] = recipe.Phases[batch.Phase].Name, ["start_utc"] = now, ["end_utc"] = null, ["planned_minutes"] = Math.Round(batch.Planned[batch.Phase] / 60.0, 2), ["actual_minutes"] = null, ["interrupted"] = false });
        }

        /// <summary>
        /// A phase has run its planned time. The next phase starts at once; after
        /// the last one the batch discharges: an intermediate goes into stock for
        /// the fed line, and the order completes with its last batch.
        /// </summary>
        private void EndPhase(LineRuntime line)
        {
            var unit = line.Line.Batch!;
            var batch = line.CurrentBatch!;
            var recipe = unit.Recipes.First(r => r.Id == batch.Recipe);
            string key = FormattableString.Invariant($"{batch.Id}-{batch.Phase + 1:00}");
            Record("batchphases", key, "update", new() { ["end_utc"] = now, ["actual_minutes"] = Math.Round((now - batch.PhaseStart).TotalMinutes, 2), ["interrupted"] = batch.Interrupted });
            batch.Deviation = Math.Max(batch.Deviation, PhaseScore(recipe, batch, batch.Phase));
            line.Deviation = Math.Round(batch.Deviation, 3);
            if (batch.Phase + 1 < recipe.Phases.Count)
            {
                batch.Phase++;
                batch.PhaseStart = now;
                batch.Remaining = batch.Planned[batch.Phase];
                batch.Interrupted = false;
                BeginPhaseRecord(batch, recipe);
                RunBatch(line, unit);
                return;
            }
            var order = line.Current!;
            double score = Math.Round(batch.Deviation, 3);
            bool golden = score <= unit.GoldenThreshold;
            // With batch release testing the batch waits in quarantine for its lab
            // result (and, off golden under GxP, for its deviation to close).
            bool deviation = gated && !golden && model.Quality!.Deviations is not null;
            Record("batches", batch.Id, "update", new()
            {
                ["end_utc"] = now, ["status"] = gated ? "Quarantine" : "Released", ["yield_percent"] = batch.Yield,
                ["deviation_score"] = score, ["golden"] = golden, ["released_utc"] = gated ? null : now
            });
            order.Good = Math.Round(order.Good + batch.Size, 3);
            line.CurrentBatch = null;
            LineRuntime? fed = null;
            bool intermediate = unit.Feeds is { } f && f.Consumption.Any(c => c.Intermediate == order.Sku);
            var shift = BatchShift(recipe, batch.Yield);
            if (!gated)
            {
                if (intermediate)
                {
                    intermediateStock[order.Sku].Add(new StockBatch { Id = batch.Id, Remaining = batch.Size, Shift = shift });
                    intermediateIncoming[order.Sku] = Math.Round(intermediateIncoming[order.Sku] - batch.Size, 3);
                    fed = lines[unit.Feeds!.Line];
                }
            }
            else
            {
                var pending = new PendingBatch
                {
                    Id = batch.Id, Order = order.Id, Line = line.Line.Id, Sku = order.Sku, Recipe = recipe.Id, Size = batch.Size, Yield = batch.Yield,
                    Intermediate = intermediate, Shift = shift, LabPending = true, DeviationOpen = deviation
                };
                pendingBatches[batch.Id] = pending;
                if (!intermediate) unreleased[order.Id] = unreleased.GetValueOrDefault(order.Id) + 1;
                SampleBatch(pending);
                if (deviation) OpenDeviation(pending, recipe, batch, score);
            }
            if (order.Good >= order.GoodTarget - 1e-6) CompleteOrder(line);
            else RunBatch(line, unit);
            if (fed is { State: "Idle" }) Dispatch(fed);
        }

        private void OperatorCompliance(ProductionOrder order, string instruction, bool certified, string reason, DateTimeOffset? signedOff)
        {
            string id = NextId("OC");
            Record("operatorcompliance", id, "insert", new()
            {
                ["compliance_id"] = id, ["run_id"] = order.Run, ["operator_id"] = order.Operator, ["instruction_id"] = instruction,
                ["certified"] = certified, ["reason"] = reason, ["checked_utc"] = now, ["signoff_utc"] = signedOff
            });
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
                case "phase": EndPhase(line); break;
                default: Fail(line, Mode(line.PlantedFailures.Dequeue()), planted: true); break;
            }
        }

        /// <summary>Accounts for output and running hours since the line started running.</summary>
        private void LeaveRunning(LineRuntime line)
        {
            double minutes = (now - line.RunningSince).TotalMinutes;
            if (line.CurrentBatch is { } batch) batch.Remaining -= (long)Math.Round((now - line.RunningSince).TotalSeconds);
            else
            {
                double gross = line.Rate * minutes;
                var order = line.Current!;
                order.Good += gross * (1 - line.Reject / 100);
                order.Rejected += gross * line.Reject / 100;
            }
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
            // A batch order replenishing a fed line has no sales order: its batches are the stock.
            if (order.SalesOrder is not null)
            {
                // Without a Shipping area an order is done when produced; with one, when shipped.
                if (model.Shipping is null)
                    Record("salesorders", order.SalesOrder, "update", new() { ["status"] = "Completed", ["completed_utc"] = now, ["on_time"] = now <= order.Due });
                else
                    Record("salesorders", order.SalesOrder, "update", new() { ["status"] = "Produced" });
                if (model.Quality is not null || model.Shipping is not null) CreateLot(line, order, good);
            }
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
            if (gated && line.Line.Batch is not null)
            {
                CreateBatchLot(line, order, cases);
                return;
            }
            string lot = NextId("LOT");
            Record("lots", lot, "insert", new()
            {
                ["lot_id"] = lot, ["production_order"] = order.Id, ["sales_order"] = order.SalesOrder!, ["sku"] = order.Sku,
                ["line_id"] = line.Line.Id, ["cases"] = cases, ["produced_utc"] = now,
                ["status"] = model.Quality is null ? "Released" : "Quarantine", ["released_utc"] = model.Quality is null ? now : null, ["shipped_utc"] = null
            });
            if (model.Quality is not { } quality)
            {
                Release(lot, order.Ref(), cases);
                return;
            }
            string sample = NextId("QS");
            var columns = new Dictionary<string, object?> { ["sample_id"] = sample, ["lot_id"] = lot, ["line_id"] = line.Line.Id, ["sku"] = order.Sku, ["sampled_utc"] = now, ["result_utc"] = null, ["status"] = "Pending", ["passed"] = null };
            if (batchPlant) columns["batch_id"] = null; // batch plants' sample table also names batches
            Record("qualitysamples", sample, "insert", columns);
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
                double value = PlantQualityValues.Value(model, parameter, lineId, order.Sku, produced, order.QualityShift?.GetValueOrDefault(parameter.Name) ?? 0);
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
            var columns = new Dictionary<string, object?> { ["hold_id"] = hold, ["lot_id"] = lot, ["sample_id"] = sample, ["reason"] = string.Join("; ", failed), ["created_utc"] = now, ["released_utc"] = null, ["disposition"] = null };
            if (batchPlant) columns["batch_id"] = null;
            Record("qualityholds", hold, "insert", columns);
            Record("lots", lot, "update", new() { ["status"] = "On Hold" });
            At(now.AddMinutes(random.Uniform(quality.HoldMinutes)), StatePriority, new Event(Kind.HoldRelease, Id: hold, Ref: lot, Number: cases, Order: order));
        }

        // ---------------- batch release: lab results, holds and deviations ----------------

        /// <summary>
        /// What a batch's yield does to its quality: for each release test with a
        /// yield sensitivity, the shift from the parameter's usual value. Fed lines
        /// inherit it. Null when no release test depends on yield.
        /// </summary>
        private Dictionary<string, double>? BatchShift(PlantRecipe recipe, double yield)
        {
            Dictionary<string, double>? shift = null;
            foreach (var test in model.Quality?.BatchRelease?.Parameters ?? [])
                if (test.YieldSensitivity != 0)
                    (shift ??= new(StringComparer.Ordinal))[test.Parameter] = Math.Round(test.YieldSensitivity * (yield - recipe.Golden.YieldTarget), 4);
            return shift;
        }

        private void SampleBatch(PendingBatch batch)
        {
            string sample = NextId("QS");
            Record("qualitysamples", sample, "insert", new() { ["sample_id"] = sample, ["lot_id"] = null, ["batch_id"] = batch.Id, ["line_id"] = batch.Line, ["sku"] = batch.Sku, ["sampled_utc"] = now, ["result_utc"] = null, ["status"] = "Pending", ["passed"] = null });
            var random = new PlantRandom(model.Seed, "lab:" + sample);
            At(now.AddMinutes(random.Uniform(model.Quality!.BatchRelease!.LabMinutes)), StatePriority, new Event(Kind.BatchLabResult, Line: batch.Line, Id: sample, Ref: batch.Id));
        }

        /// <summary>
        /// A batch's release results: each tested parameter at its usual value for the
        /// SKU, moved by the batch's yield shift, plus the sample's own noise, judged
        /// against the release limits. A failure holds the batch for investigation.
        /// </summary>
        private void BatchLabResult(string sample, string batchId)
        {
            var quality = model.Quality!;
            var batch = pendingBatches[batchId];
            var random = new PlantRandom(model.Seed, "lab:" + sample);
            random.Uniform(quality.BatchRelease!.LabMinutes);
            var failed = new List<string>();
            foreach (var test in quality.BatchRelease.Parameters)
            {
                var parameter = quality.Parameters.First(p => p.Name == test.Parameter);
                double lower = test.Lower ?? parameter.Lower, upper = test.Upper ?? parameter.Upper;
                double value = Math.Round(parameter.Target + parameter.SkuOffsets.GetValueOrDefault(batch.Sku) + (batch.Shift?.GetValueOrDefault(test.Parameter) ?? 0) +
                    parameter.Sigma * PlantRandom.Noise(model.Seed, $"batchlab:{batch.Id}:{test.Parameter}", 0), 3);
                bool ok = value >= lower && value <= upper;
                if (!ok) failed.Add(FormattableString.Invariant($"{parameter.Name} {value} {(value > upper ? "above" : "below")} {(value > upper ? upper : lower)}"));
                string result = NextId("QR");
                Record("qualityresults", result, "insert", new() { ["result_id"] = result, ["sample_id"] = sample, ["parameter"] = parameter.Name, ["value"] = value, ["unit"] = parameter.Unit, ["lower_limit"] = lower, ["upper_limit"] = upper, ["in_spec"] = ok, ["result_utc"] = now });
            }
            Record("qualitysamples", sample, "update", new() { ["result_utc"] = now, ["status"] = "Complete", ["passed"] = failed.Count == 0 });
            if (failed.Count == 0)
            {
                batch.LabPending = false;
                TryReleaseBatch(batch);
                return;
            }
            string hold = NextId("QH");
            Record("qualityholds", hold, "insert", new() { ["hold_id"] = hold, ["lot_id"] = null, ["batch_id"] = batch.Id, ["sample_id"] = sample, ["reason"] = string.Join("; ", failed), ["created_utc"] = now, ["released_utc"] = null, ["disposition"] = null });
            Record("batches", batch.Id, "update", new() { ["status"] = "On Hold" });
            At(now.AddMinutes(random.Uniform(quality.HoldMinutes)), StatePriority, new Event(Kind.BatchHoldRelease, Id: hold, Ref: batch.Id));
        }

        /// <summary>A batch off the golden profile gets a deviation naming its worst phase; a QA person closes it later.</summary>
        private void OpenDeviation(PendingBatch pending, PlantRecipe recipe, BatchState batch, double score)
        {
            int worst = Enumerable.Range(0, recipe.Phases.Count).OrderByDescending(i => PhaseScore(recipe, batch, i)).ThenBy(i => i).First();
            var phase = recipe.Phases[worst];
            double minutes = batch.Planned[worst] / 60.0, lag = batch.Lag.Length > worst ? batch.Lag[worst] : 0;
            string description = lag / PlantBatchThermal.LagScaleC > Math.Abs(minutes - recipe.Golden.PhaseMinutes[worst]) / Math.Max(0.5, (phase.Minutes.Maximum - phase.Minutes.Minimum) / 2)
                ? FormattableString.Invariant($"{phase.Name} temperature trailed the setpoint by up to {lag:0.#} °C; deviation score {score:0.00} against the golden profile")
                : FormattableString.Invariant($"{phase.Name} ran {minutes:0.#} min against a golden {recipe.Golden.PhaseMinutes[worst]:0.#} min (normal {phase.Minutes.Minimum:0.#}–{phase.Minutes.Maximum:0.#}); deviation score {score:0.00}");
            string id = NextId("DEV");
            Record("deviations", id, "insert", new() { ["deviation_id"] = id, ["batch_id"] = pending.Id, ["phase"] = phase.Name, ["description"] = description, ["opened_utc"] = now, ["closed_utc"] = null, ["qa_approver_id"] = null });
            var random = new PlantRandom(model.Seed, "deviation:" + id);
            At(now.AddHours(random.Uniform(model.Quality!.Deviations!.CloseHours)), StatePriority, new Event(Kind.DeviationClose, Id: id, Ref: pending.Id));
        }

        /// <summary>QA closes the deviation: the QA person on shift approves it (the first QA person when none is).</summary>
        private void CloseDeviation(string deviation, string batchId)
        {
            var shift = ShiftAt(now).Name;
            var approver = model.QualityStaff.FirstOrDefault(q => q.Shift == shift) ?? model.QualityStaff[0];
            Record("deviations", deviation, "update", new() { ["closed_utc"] = now, ["qa_approver_id"] = approver.Id });
            var batch = pendingBatches[batchId];
            batch.DeviationOpen = false;
            TryReleaseBatch(batch);
        }

        /// <summary>
        /// Releases a quarantined batch once its results passed (or its hold was
        /// released) and its deviation, if any, is closed. An intermediate goes into
        /// stock for the fed line; a direct-sale batch may complete its lot's release.
        /// </summary>
        private void TryReleaseBatch(PendingBatch batch)
        {
            if (batch.LabPending || batch.DeviationOpen) return;
            pendingBatches.Remove(batch.Id);
            Record("batches", batch.Id, "update", new() { ["status"] = "Released", ["released_utc"] = now });
            if (batch.Intermediate)
            {
                intermediateStock[batch.Sku].Add(new StockBatch { Id = batch.Id, Remaining = batch.Size, Shift = batch.Shift });
                intermediateIncoming[batch.Sku] = Math.Round(intermediateIncoming[batch.Sku] - batch.Size, 3);
                var fed = lines[lines[batch.Line].Line.Batch!.Feeds!.Line];
                if (fed.State == "Idle") Dispatch(fed);
                return;
            }
            int left = unreleased[batch.Order] - 1;
            if (left > 0) { unreleased[batch.Order] = left; return; }
            unreleased.Remove(batch.Order);
            if (waitingLots.Remove(batch.Order, out var waiting))
            {
                Record("lots", waiting.Lot, "update", new() { ["status"] = "Released", ["released_utc"] = now });
                Release(waiting.Lot, waiting.Order, waiting.Cases);
            }
        }

        /// <summary>
        /// A batch-unit order's lot: its batches were tested one by one, so the lot
        /// has no sample of its own and is released when its last batch is.
        /// </summary>
        private void CreateBatchLot(LineRuntime line, ProductionOrder order, long cases)
        {
            string lot = NextId("LOT");
            bool ready = unreleased.GetValueOrDefault(order.Id) == 0;
            Record("lots", lot, "insert", new()
            {
                ["lot_id"] = lot, ["production_order"] = order.Id, ["sales_order"] = order.SalesOrder!, ["sku"] = order.Sku,
                ["line_id"] = line.Line.Id, ["cases"] = cases, ["produced_utc"] = now,
                ["status"] = ready ? "Released" : "Quarantine", ["released_utc"] = ready ? now : null, ["shipped_utc"] = null
            });
            if (ready) Release(lot, order.Ref(), cases);
            else waitingLots[order.Id] = new WaitingLot(lot, order.Ref(), cases);
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
            if (line.CurrentBatch is { } batch) batch.Interrupted = true; // the phase resumes with its remaining time after the repair
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
                var skilled = technicians.Where(t => !t.Busy && t.Person.Shift == shift && t.Person.Skills.Contains(ticket.Skill))
                    .OrderBy(t => t.Completed).ThenBy(t => t.Person.Id, StringComparer.Ordinal);
                bool critical = certification is not null && certification.SafetyCritical.Contains(ticket.Code);
                bool certified = true;
                TechnicianRuntime? technician;
                if (!critical) technician = skilled.FirstOrDefault();
                else
                {
                    // Safety-critical work: a certified technician first; otherwise wait,
                    // or (assignAnyway) a skilled technician without a current certificate.
                    technician = skilled.FirstOrDefault(t => Certified(t.Person.Id, ticket.Skill));
                    if (technician is null && certification!.AssignAnyway && (technician = skilled.FirstOrDefault()) is not null) certified = false;
                }
                if (technician is null) continue;
                technician.Busy = true;
                ticket.Technician = technician.Person.Id;
                Record("maintenancetickets", ticket.Id, "update", new() { ["status"] = "Assigned", ["technician_id"] = ticket.Technician, ["assigned_utc"] = now });
                string reason = critical ? CertificateReason(technician.Person.Id, ticket.Skill) : "";
                StatusHistory(ticket, "Assigned", certified ? "Assigned to technician" : $"Assigned without a current certificate ({reason})");
                if (critical)
                {
                    string compliance = NextId("WC");
                    Record("workcompliance", compliance, "insert", new()
                    {
                        ["compliance_id"] = compliance, ["ticket_id"] = ticket.Id, ["technician_id"] = ticket.Technician, ["skill"] = ticket.Skill,
                        ["safety_critical"] = true, ["certified"] = certified, ["reason"] = reason, ["checked_utc"] = now
                    });
                }
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
                // A cleaning PM ends the drifts tied to it (the first completion after each drift began).
                for (int i = 0; i < model.Drifts.Count; i++)
                    if (model.Drifts[i].EndsWith == ticket.Code && now >= model.Drifts[i].FromUtc && !driftEnded.ContainsKey(i)) driftEnded[i] = now;
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
