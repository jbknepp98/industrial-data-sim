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

/// <summary>The complete result of simulating a plant over [StartUtc, EndUtc).</summary>
public sealed record PlantRun(PlantModel Model, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    IReadOnlyList<PlantRecordChange> Changes, IReadOnlyList<PlantLineSegment> Segments, IReadOnlyList<PlantFailure> Failures);
