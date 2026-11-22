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
    string? Sku, string? Order, string? Reason, string? Unit, double CasesPerMinute, double RejectPercent);

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
