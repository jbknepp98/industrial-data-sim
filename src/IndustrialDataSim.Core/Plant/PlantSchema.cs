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

    /// <summary>Every table purpose the simulator can create (for removal and lookups).</summary>
    public static IEnumerable<PlantTable> AllTables => Tables.Concat(QualityTables).Append(LotsTable).Concat(ShippingTables);

    /// <summary>The tables this plant uses: the core loop plus its enabled areas.</summary>
    public static IReadOnlyList<PlantTable> TablesFor(PlantModel model)
    {
        var tables = Tables.ToList();
        if (model.Quality is not null || model.Shipping is not null) tables.Add(LotsTable);
        if (model.Quality is not null) tables.AddRange(QualityTables);
        if (model.Shipping is not null) tables.AddRange(ShippingTables);
        return tables;
    }

    public static PlantTable Table(string purpose) => AllTables.First(t => t.Purpose == purpose);

    /// <summary>The physical table name: the plant's lower-case prefix plus the purpose.</summary>
    public static string PhysicalName(PlantModel model, string purpose) => model.TablePrefix + purpose;
}
