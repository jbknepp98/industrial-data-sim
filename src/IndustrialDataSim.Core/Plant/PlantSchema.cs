namespace IndustrialDataSim.Core.Plant;

/// <summary>A column: Postgres type name (text, integer, bigint, numeric, boolean, timestamptz).</summary>
public sealed record PlantColumn(string Name, string Type, bool Nullable = true);

/// <summary>A table purpose with its natural key (first column) and business columns.</summary>
public sealed record PlantTable(string Purpose, string Description, IReadOnlyList<PlantColumn> Columns)
{
    public string Key => Columns[0].Name;
}

/// <summary>
/// The relational contract of a plant: one table per purpose, physical name
/// <c>simulationname_purpose</c> (lower case, owner decision). Connector rules
/// (from the atlas-demo-database conventions) are applied to every table:
/// natural single-column keys, a <c>row_version</c> watermark set by trigger from
/// one shared sequence, a readable <c>modified_utc</c> (the simulated change
/// time, not a watermark), and soft-delete columns. Tests assert every engine
/// change matches this schema exactly.
/// </summary>
public static class PlantSchema
{
    public static readonly IReadOnlyList<PlantColumn> StandardColumns =
    [
        new("row_version", "bigint", Nullable: false),
        new("modified_utc", "timestamptz", Nullable: false),
        new("is_deleted", "boolean", Nullable: false),
        new("deleted_utc", "timestamptz")
    ];

    private static PlantColumn Key(string name) => new(name, "text", Nullable: false);
    private static PlantColumn Text(string name) => new(name, "text");
    private static PlantColumn Number(string name) => new(name, "numeric");
    private static PlantColumn Whole(string name) => new(name, "bigint");
    private static PlantColumn Flag(string name) => new(name, "boolean");
    private static PlantColumn Time(string name) => new(name, "timestamptz");

    public static readonly IReadOnlyList<PlantTable> Tables =
    [
        new("lines", "Production lines", [Key("line_id"), Text("line_name"), Whole("changeover_minutes")]),
        new("equipment", "Units on each line", [Key("equipment_id"), Text("line_id"), Text("equipment_name"), Text("equipment_class")]),
        new("skus", "Products", [Key("sku"), Text("description"), Number("cases_per_minute"), Number("reject_percent"), Number("fill_target"), Text("fill_unit")]),
        new("shifts", "Shift pattern (UTC hours)", [Key("shift_name"), Whole("start_hour_utc"), Whole("hours")]),
        new("customers", "Customers", [Key("customer_id"), Text("customer_name")]),
        new("employees", "Operators and technicians", [Key("employee_id"), Text("employee_name"), Text("role"), Text("shift_name"), Text("line_id")]),
        new("certifications", "Technician skills", [Key("certification_id"), Text("employee_id"), Text("skill")]),
        new("parts", "Spare parts", [Key("part_id"), Text("description"), Number("unit_cost")]),
        new("failurecodes", "Failure modes", [Key("failure_code"), Text("equipment_id"), Text("description"), Text("skill"), Text("priority")]),
        new("salesorders", "Customer orders", [Key("sales_order"), Text("customer_id"), Text("sku"), Number("quantity_cases"), Time("created_utc"), Time("due_utc"), Text("status"), Time("completed_utc"), Flag("on_time")]),
        new("productionorders", "Work released to a line", [Key("production_order"), Text("sales_order"), Text("sku"), Text("line_id"), Number("quantity_cases"), Time("released_utc"), Time("due_utc"), Text("status"), Time("started_utc"), Time("completed_utc"), Whole("good_cases"), Whole("reject_cases")]),
        new("productionruns", "Execution of an order on a line", [Key("run_id"), Text("production_order"), Text("line_id"), Text("sku"), Text("operator_id"), Time("start_utc"), Time("end_utc"), Whole("good_cases"), Whole("reject_cases")]),
        new("downtimeevents", "Planned and unplanned downtime", [Key("downtime_id"), Text("line_id"), Text("equipment_id"), Text("category"), Text("reason"), Text("failure_code"), Time("start_utc"), Time("detected_utc"), Time("end_utc"), Number("duration_minutes")]),
        new("maintenancetickets", "Corrective and preventive work orders", [Key("ticket_id"), Text("ticket_type"), Text("equipment_id"), Text("failure_code"), Text("priority"), Text("status"), Time("created_utc"), Text("downtime_id"), Text("technician_id"), Time("assigned_utc"), Time("started_utc"), Time("completed_utc")]),
        new("maintenanceticketstatus", "Ticket status history", [Key("status_id"), Text("ticket_id"), Text("status"), Time("status_utc"), Text("technician_id"), Text("note")]),
        new("partsusage", "Parts used by tickets", [Key("usage_id"), Text("ticket_id"), Text("part_id"), Whole("quantity"), Time("used_utc")])
    ];

    /// <summary>Tables of the optional areas, created only when the model includes them.</summary>
    public static readonly IReadOnlyList<PlantTable> QualityTables =
    [
        new("qualityspecs", "Specification limits per parameter and SKU", [Key("spec_id"), Text("parameter"), Text("sku"), Text("unit"), Number("target"), Number("lower_limit"), Number("upper_limit")]),
        new("qualitysamples", "Lot samples taken at production completion", [Key("sample_id"), Text("lot_id"), Text("line_id"), Text("sku"), Time("sampled_utc"), Time("result_utc"), Text("status"), Flag("passed")]),
        new("qualityresults", "Lab results per parameter", [Key("result_id"), Text("sample_id"), Text("parameter"), Number("value"), Text("unit"), Number("lower_limit"), Number("upper_limit"), Flag("in_spec"), Time("result_utc")]),
        new("qualityholds", "Lots held for out-of-spec results", [Key("hold_id"), Text("lot_id"), Text("sample_id"), Text("reason"), Time("created_utc"), Time("released_utc"), Text("disposition")])
    ];

    public static readonly PlantTable LotsTable = new("lots", "Finished-goods lots, one per completed order",
        [Key("lot_id"), Text("production_order"), Text("sales_order"), Text("sku"), Text("line_id"), Whole("cases"), Time("produced_utc"), Text("status"), Time("released_utc"), Time("shipped_utc")]);

    public static readonly IReadOnlyList<PlantTable> ShippingTables =
    [
        new("inventorytransactions", "Finished-goods receipts and shipments", [Key("transaction_id"), Text("lot_id"), Text("sku"), Text("transaction_type"), Whole("quantity_cases"), Time("transaction_utc")]),
        new("shipments", "Shipments against sales orders", [Key("shipment_id"), Text("sales_order"), Text("lot_id"), Text("carrier"), Whole("quantity_cases"), Time("planned_utc"), Time("shipped_utc"), Text("status")])
    ];

    /// <summary>Certification compliance (maintenance.certification): certificates and safety-critical assignments.</summary>
    public static readonly IReadOnlyList<PlantTable> CertificationTables =
    [
        new("trainingrecords", "Certificates per employee and skill or work instruction, with expiry", [Key("record_id"), Text("employee_id"), Text("skill"), Time("issued_utc"), Time("expires_utc"), Text("status")]),
        new("workcompliance", "Certificate check for each safety-critical ticket assignment", [Key("compliance_id"), Text("ticket_id"), Text("technician_id"), Text("skill"), Flag("safety_critical"), Flag("certified"), Text("reason"), Time("checked_utc")])
    ];

    /// <summary>Operator certification (maintenance.certification.operators): work instructions and run-start checks.</summary>
    public static readonly IReadOnlyList<PlantTable> OperatorCertificationTables =
    [
        new("workinstructions", "Work instructions per line and SKUs (comma-separated)", [Key("instruction_id"), Text("line_id"), Text("skus"), Whole("valid_days")]),
        new("operatorcompliance", "Operator certificate check when each run starts", [Key("compliance_id"), Text("run_id"), Text("operator_id"), Text("instruction_id"), Flag("certified"), Text("reason"), Time("checked_utc"), Time("signoff_utc")])
    ];

    /// <summary>Batch units (lines with kind "batch"): recipes, batches and their phases.</summary>
    public static readonly IReadOnlyList<PlantTable> BatchTables =
    [
        new("recipes", "Batch recipes, one per SKU made on a batch unit", [Key("recipe_id"), Text("sku"), Whole("phase_count")]),
        new("recipephases", "Recipe phases with duration range and setpoint ramps (JSON)", [Key("recipe_phase_id"), Text("recipe_id"), Whole("sequence"), Text("phase"), Number("minutes_min"), Number("minutes_max"), Text("setpoints_json")]),
        new("batches", "Batches run on batch units; size is in the unit's capacity unit", [Key("batch_id"), Text("production_order"), Text("recipe_id"), Text("line_id"), Time("start_utc"), Time("end_utc"), Number("size"), Text("status")]),
        new("batchphases", "Executed phases of each batch; interrupted phases include their Down time", [Key("batch_phase_id"), Text("batch_id"), Whole("sequence"), Text("phase"), Time("start_utc"), Time("end_utc"), Number("planned_minutes"), Number("actual_minutes"), Flag("interrupted")])
    ];

    /// <summary>A batch unit feeding a packaging line (batch.feeds): genealogy from packaging orders to batches.</summary>
    public static readonly PlantTable BatchConsumptionTable = new("batchconsumption", "Intermediate taken from each batch by a fed packaging order",
        [Key("consumption_id"), Text("batch_id"), Text("production_order"), Number("quantity"), Time("consumed_utc")]);

    /// <summary>Every table purpose the simulator can create (for removal and lookups).</summary>
    public static IEnumerable<PlantTable> AllTables => Tables.Concat(QualityTables).Append(LotsTable).Concat(ShippingTables)
        .Concat(CertificationTables).Concat(OperatorCertificationTables).Concat(BatchTables).Append(BatchConsumptionTable);

    /// <summary>The tables this plant uses: the core loop plus its enabled areas.</summary>
    public static IReadOnlyList<PlantTable> TablesFor(PlantModel model)
    {
        var tables = Tables.ToList();
        if (model.Quality is not null || model.Shipping is not null) tables.Add(LotsTable);
        if (model.Quality is not null) tables.AddRange(QualityTables);
        if (model.Shipping is not null) tables.AddRange(ShippingTables);
        if (model.Maintenance.Certification is { } certification)
        {
            tables.AddRange(CertificationTables);
            if (certification.Operators is not null) tables.AddRange(OperatorCertificationTables);
        }
        if (model.Lines.Any(l => l.Batch is not null)) tables.AddRange(BatchTables);
        if (model.Lines.Any(l => l.Batch?.Feeds is not null)) tables.Add(BatchConsumptionTable);
        return tables;
    }

    public static PlantTable Table(string purpose) => AllTables.First(t => t.Purpose == purpose);

    /// <summary>The physical table name: the plant's lower-case prefix plus the purpose.</summary>
    public static string PhysicalName(PlantModel model, string purpose) => model.TablePrefix + purpose;
}
