namespace IndustrialDataSim.Core.Plant;

/// <summary>
/// A validated plant: reference data, demand, maintenance behavior and planted
/// incidents for the Production → Maintenance loop. Only the loader builds it.
/// Times are UTC. The plant runs from StartUtc to EndUtc, or open-ended when
/// EndUtc is null (the offline preview then stops at a requested horizon).
/// </summary>
public sealed record PlantModel(
    string Simulation,
    string TablePrefix,
    string TagRoot,
    ulong Seed,
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc,
    DateTimeOffset AnchorUtc,
    int SampleIntervalMs,
    IReadOnlyList<PlantShift> Shifts,
    IReadOnlyList<PlantSku> Skus,
    IReadOnlyList<PlantLine> Lines,
    IReadOnlyList<PlantPerson> Operators,
    IReadOnlyList<PlantPerson> Technicians,
    IReadOnlyList<PlantCustomer> Customers,
    IReadOnlyList<PlantPart> Parts,
    PlantDemand Demand,
    PlantMaintenance Maintenance,
    IReadOnlyList<PlantIncident> Incidents)
{
    /// <summary>Optional Quality area: lot samples, lab results and holds. Null when not included.</summary>
    public PlantQuality? Quality { get; init; }

    /// <summary>Optional Shipping area: lots, inventory and shipments. Null when not included.</summary>
    public PlantShipping? Shipping { get; init; }
}

/// <summary>A shift starts at StartHour (UTC, whole hours) every day and lasts Hours.</summary>
public sealed record PlantShift(string Name, int StartHour, int Hours);

/// <summary>Rate is good cases per running minute; RejectPercent of output is scrap.</summary>
public sealed record PlantSku(string Code, string Description, double CasesPerMinute, double RejectPercent,
    double FillTarget, string FillUnit);

public sealed record PlantLine(string Id, string Name, int ChangeoverMinutes, IReadOnlyList<string> Skus,
    IReadOnlyList<PlantEquipment> Equipment);

/// <summary>Class "filler" adds a fill-volume signal; every unit has status and vibration.</summary>
public sealed record PlantEquipment(string Id, string Name, string Class);

/// <summary>An operator or technician. Skills apply to technicians only.</summary>
public sealed record PlantPerson(string Id, string Name, string Shift, string? Line, IReadOnlyList<string> Skills);

public sealed record PlantCustomer(string Id, string Name);

public sealed record PlantPart(string Id, string Description, decimal UnitCost);

public sealed record PlantRange(double Minimum, double Maximum);

/// <summary>Sales orders arrive at OrdersPerDay on average; each becomes one production order.</summary>
public sealed record PlantDemand(double OrdersPerDay, PlantRange CasesPerOrder, PlantRange DueDays);

public sealed record PlantMaintenance(
    int DetectionMinutes,
    PlantRange ResponseMinutes,
    IReadOnlyList<PlantFailureMode> FailureModes,
    IReadOnlyList<PlantPreventive> Preventive,
    PlantMinorStops MinorStops);

/// <summary>
/// Failures occur after an exponentially distributed amount of running time
/// with mean MtbfRunHours. WarningHours before each failure the unit's
/// vibration rises, so the time series foreshadows the maintenance record.
/// </summary>
public sealed record PlantFailureMode(string Code, string Line, string Equipment, string Description,
    double MtbfRunHours, PlantRange RepairMinutes, string Skill, string Priority, double WarningHours,
    IReadOnlyList<PlantPartUse> Parts);

public sealed record PlantPartUse(string Part, int Quantity);

public sealed record PlantPreventive(string Code, string Line, string Equipment, string Description,
    int EveryDays, int StartHour, int DurationMinutes, string Skill);

/// <summary>Short stops the operator clears; always shorter than the detection threshold.</summary>
public sealed record PlantMinorStops(double PerRunHour, PlantRange Minutes);

/// <summary>A planted failure at AtUtc (or at the next running moment after it).</summary>
public sealed record PlantIncident(string FailureMode, DateTimeOffset AtUtc, string Note);

/// <summary>
/// A measured product property. The line's in-line analyzer tag and the lab
/// result both come from the same value function (target + SKU offset +
/// excursion shift + noise), so the records and the time series agree.
/// </summary>
public sealed record PlantQualityParameter(string Name, string Unit, double Target, double Lower, double Upper,
    double Sigma, IReadOnlyDictionary<string, double> SkuOffsets);

/// <summary>A planted quality excursion: Shift is added to Parameter from FromUtc until UntilUtc (one SKU or all).</summary>
public sealed record PlantExcursion(string Parameter, DateTimeOffset FromUtc, DateTimeOffset UntilUtc, double Shift, string? Sku, string Note);

/// <summary>
/// Each finished order becomes a lot that is sampled at completion. Results
/// arrive after LabMinutes; any result outside its limits puts the lot on hold
/// for HoldMinutes (investigation and retest) before release.
/// </summary>
public sealed record PlantQuality(IReadOnlyList<PlantQualityParameter> Parameters, PlantRange LabMinutes,
    PlantRange HoldMinutes, IReadOnlyList<PlantExcursion> Excursions);

/// <summary>Released lots ship against their sales order after DelayHours, with one of the carriers.</summary>
public sealed record PlantShipping(PlantRange DelayHours, IReadOnlyList<string> Carriers);
