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
    IReadOnlyList<PlantEquipment> Equipment)
{
    /// <summary>"packaging" (a continuous line, the default) or "batch" (a batch unit running recipes).</summary>
    public string Kind { get; init; } = "packaging";

    /// <summary>The batch unit's capacity and recipes; set exactly when Kind is "batch".</summary>
    public PlantBatchUnit? Batch { get; init; }
}

/// <summary>
/// A batch unit: each batch holds at most Capacity (in Unit, for example kg or
/// L) and runs its SKU's recipe, one phase after another. Orders for its SKUs
/// are in Unit and run as ceil(quantity / Capacity) batches. With Feeds set,
/// some of its SKUs are intermediates consumed by a packaging line.
/// </summary>
public sealed record PlantBatchUnit(double Capacity, string Unit, IReadOnlyList<PlantRecipe> Recipes, PlantFeeds? Feeds)
{
    public PlantRecipe RecipeFor(string sku) => Recipes.First(r => r.Sku == sku);
}

/// <summary>One recipe per SKU made on a batch unit: its phases in order.</summary>
public sealed record PlantRecipe(string Id, string Sku, IReadOnlyList<PlantPhase> Phases);

/// <summary>A start and end value; setpoints ramp linearly from Start to End over a phase's running time.</summary>
public sealed record PlantRamp(double Start, double End)
{
    public double At(double progress) => Start + (End - Start) * progress;
}

/// <summary>A recipe phase (Charge, Heat, React, Hold, Cool, Discharge …): its duration range and setpoint ramps.</summary>
public sealed record PlantPhase(string Name, PlantRange Minutes, PlantRamp TemperatureC, PlantRamp PressureBar, PlantRamp AgitatorRpm,
    PlantRamp LevelPercent);

/// <summary>
/// A batch unit feeding a packaging line: each case of a consuming SKU uses
/// PerCase of the intermediate (in the batch unit's Unit). A packaging order
/// for a consuming SKU starts only when released batches hold enough of its
/// intermediate, and consumes them first in, first out.
/// </summary>
public sealed record PlantFeeds(string Line, IReadOnlyList<PlantConsumption> Consumption);

public sealed record PlantConsumption(string Sku, string Intermediate, double PerCase);

/// <summary>Class "filler" adds a fill-volume signal; every unit has status and vibration. Batch units use reactor, agitator, pump, jacket and valve.</summary>
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
    PlantMinorStops MinorStops)
{
    /// <summary>Optional certification compliance: certificates with expiry for technicians and operators. Null when not included.</summary>
    public PlantCertification? Certification { get; init; }
}

/// <summary>
/// One certificate issue: Employee holds Skill (a technician skill, or an
/// operator's work-instruction id) from IssuedUtc for the owning section's
/// valid days. A later issue for the same pair is a renewal.
/// </summary>
public sealed record PlantCertificateIssue(string Employee, string Skill, DateTimeOffset IssuedUtc);

/// <summary>
/// Certification compliance. Technicians hold a certificate per skill
/// (Issued plus Renewals, each lasting ValidDays). A safety-critical ticket
/// (its failure or PM code is in SafetyCritical) goes to a certified
/// technician when one is free and on shift; otherwise it waits
/// (WhenNoneCertified "wait") or goes to a skilled but uncertified technician
/// ("assignAnyway"). A technician/skill pair with no issue holds no certificate.
/// </summary>
public sealed record PlantCertification(int ValidDays, IReadOnlyList<PlantCertificateIssue> Issued,
    IReadOnlyList<PlantCertificateIssue> Renewals, IReadOnlyList<string> SafetyCritical, string WhenNoneCertified)
{
    /// <summary>Optional operator side: work instructions per line and SKU. Null when not included.</summary>
    public PlantOperatorCertification? Operators { get; init; }

    public bool AssignAnyway => WhenNoneCertified == "assignAnyway";
}

/// <summary>A work instruction for running some SKUs on one line; operators hold it for ValidDays from each issue.</summary>
public sealed record PlantWorkInstruction(string Id, string Line, IReadOnlyList<string> Skus, int ValidDays,
    IReadOnlyList<PlantCertificateIssue> Issued);

/// <summary>
/// When a run starts, the shift's operator must hold a current certificate for
/// the work instruction covering that line and SKU. If not: "runAnyway" (the
/// run starts, at RejectPercentWhenUncertified when set) or "supervisorSignoff"
/// (a sign-off is recorded and the run starts SignoffMinutes later).
/// </summary>
public sealed record PlantOperatorCertification(IReadOnlyList<PlantWorkInstruction> WorkInstructions, string WhenOperatorUncertified,
    int SignoffMinutes, double? RejectPercentWhenUncertified)
{
    public bool SupervisorSignoff => WhenOperatorUncertified == "supervisorSignoff";
}

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

/// <summary>
/// A planted failure at AtUtc (or at the next running moment after it). With
/// Shift set, the loader has already moved AtUtc into that shift.
/// </summary>
public sealed record PlantIncident(string FailureMode, DateTimeOffset AtUtc, string Note)
{
    public string? Shift { get; init; }
}

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
