using System.Text.Json.Serialization;

namespace IndustrialDataSim.Core.Plant;

/// <summary>
/// One row change, as a connector would observe it: an insert or an update of a
/// table row at the simulated time it happens. Sequence orders changes that
/// share a timestamp. Table is the purpose (for example "maintenancetickets");
/// the physical name adds the plant's table prefix.
/// </summary>
public sealed record PlantRecordChange(long Sequence, DateTimeOffset Utc, string Table, string Key, string Operation,
    IReadOnlyDictionary<string, object?> Columns);

/// <summary>
/// What a line was doing over [Start, End). State is Running, MinorStop,
/// Changeover, Down, Maintenance or Idle. CasesPerMinute is the gross rate while
/// Running, otherwise zero. Unit names the equipment involved in Down or
/// Maintenance (the faulted or serviced unit).
/// </summary>
public sealed record PlantLineSegment(string Line, DateTimeOffset Start, DateTimeOffset End, string State,
    string? Sku, string? Order, string? Reason, string? Unit, double CasesPerMinute, double RejectPercent)
{
    /// <summary>
    /// Batch lines: the batch and phase this segment belongs to (Running in that
    /// phase, or Down while it is interrupted). Null on packaging lines and between
    /// batches; left out of JSON when null, so packaging output is unchanged.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlantBatchSegment? Batch { get; init; }

    /// <summary>Batch lines: the BatchDeviation value during this segment (updated at the end of each phase). Null on packaging lines.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Deviation { get; init; }

    /// <summary>
    /// The running order's quality shift per parameter, inherited from the batches
    /// it consumed (a fed line). Null otherwise, so other output is unchanged.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, double>? QualityShift { get; init; }
}

/// <summary>
/// One stretch of a batch phase: Sequence is the phase's 1-based position in the
/// recipe and PlannedSeconds its drawn running time; From and To are the phase's
/// progress (0–1) at the segment's start and end. Progress advances one second
/// per second only while Running, and setpoints ramp linearly with it.
/// </summary>
public sealed record PlantBatchSegment(string BatchId, string Recipe, int Sequence, string Phase, int PlannedSeconds, double From, double To)
{
    /// <summary>How far the temperature trails the setpoint at mid-phase (a golden-batch drift); 0 on profile.</summary>
    public double LagC { get; init; }

    /// <summary>The process temperature (°C, without noise) at the segment's start; see PlantBatchThermal.</summary>
    public double StartC { get; init; } = PlantBatchThermal.AmbientC;

    /// <summary>The phase's progress at a time inside this segment.</summary>
    public double ProgressAt(PlantLineSegment segment, DateTimeOffset utc) =>
        segment.State == "Running" ? Math.Min(1, From + (utc - segment.Start).TotalSeconds / PlannedSeconds) : From;
}

/// <summary>A breakdown: the unit failed at FailedUtc and was back in service at RepairedUtc.</summary>
public sealed record PlantFailure(string Line, string Equipment, string Mode, DateTimeOffset FailedUtc,
    DateTimeOffset? RepairedUtc, double WarningHours, bool Planted);

/// <summary>
/// The result of simulating a plant over [StartUtc, EndUtc). A run resumed from
/// a snapshot starts at the snapshot time: it holds only the changes after the
/// snapshot, the segments still open at it or later, and the failures not yet
/// repaired at it. Base then carries the running totals the signals need.
/// </summary>
public sealed record PlantRun(PlantModel Model, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    IReadOnlyList<PlantRecordChange> Changes, IReadOnlyList<PlantLineSegment> Segments, IReadOnlyList<PlantFailure> Failures)
{
    /// <summary>Running totals before the first segment and change of a resumed run; null for a run from the plant start.</summary>
    public PlantRunBase? Base { get; init; }
}

/// <summary>
/// Cumulative good and reject cases per line over every segment closed before
/// a resumed run, and the warehouse stock after every earlier inventory change.
/// </summary>
public sealed record PlantRunBase(IReadOnlyDictionary<string, double> Good, IReadOnlyDictionary<string, double> Reject, double Stock);

/// <summary>
/// The complete engine state at AtUtc: every event at or after AtUtc is still
/// pending, every earlier one has run. Resuming from it produces exactly the
/// changes, segments and signals a run from the plant start produces after
/// AtUtc. State is versioned JSON; a snapshot from another EngineVersion is
/// refused, never interpreted.
/// </summary>
public sealed record PlantSnapshot(int EngineVersion, DateTimeOffset AtUtc, long ChangeSequence, string State);

/// <summary>A run, plus the snapshot captured during it when one was requested.</summary>
public sealed record PlantRunResult(PlantRun Run, PlantSnapshot? Snapshot);
