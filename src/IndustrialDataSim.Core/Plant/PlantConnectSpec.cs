using System.Text;

namespace IndustrialDataSim.Core.Plant;

/// <summary>
/// Hand-off documents for the rest of Timebase, generated from the plant model:
/// - a Connect mapping spec (every table, key, watermark, incremental query,
///   relationship and the read-only login), as Markdown and as JSON;
/// - an Atlas starter: the asset hierarchy as instances (site, lines, units,
///   sensors as separate nodes bound to Historian tags, SKUs, people) and the
///   transactional data as parameterized query properties, not instances
///   (owner modeling preference: reference work orders, don't instantiate them).
/// The Atlas starter is input for modeling (for example with the
/// atlas-data-modeling skill), not a compiled Atlas payload.
/// </summary>
public static class PlantConnectSpec
{
    /// <summary>Foreign keys: (table, column) → referenced table purpose.</summary>
    private static readonly (string Table, string Column, string References)[] Relationships =
    [
        ("equipment", "line_id", "lines"), ("employees", "line_id", "lines"), ("employees", "shift_name", "shifts"),
        ("certifications", "employee_id", "employees"), ("failurecodes", "equipment_id", "equipment"),
        ("salesorders", "customer_id", "customers"), ("salesorders", "sku", "skus"),
        ("productionorders", "sales_order", "salesorders"), ("productionorders", "line_id", "lines"), ("productionorders", "sku", "skus"),
        ("productionruns", "production_order", "productionorders"), ("productionruns", "operator_id", "employees"),
        ("downtimeevents", "line_id", "lines"), ("downtimeevents", "equipment_id", "equipment"), ("downtimeevents", "failure_code", "failurecodes"),
        ("maintenancetickets", "equipment_id", "equipment"), ("maintenancetickets", "downtime_id", "downtimeevents"), ("maintenancetickets", "technician_id", "employees"),
        ("maintenanceticketstatus", "ticket_id", "maintenancetickets"), ("partsusage", "ticket_id", "maintenancetickets"), ("partsusage", "part_id", "parts"),
        ("lots", "production_order", "productionorders"), ("lots", "sales_order", "salesorders"),
        ("qualitysamples", "lot_id", "lots"), ("qualityresults", "sample_id", "qualitysamples"), ("qualityholds", "lot_id", "lots"),
        ("inventorytransactions", "lot_id", "lots"), ("shipments", "sales_order", "salesorders"), ("shipments", "lot_id", "lots"),
        ("trainingrecords", "employee_id", "employees"), ("workcompliance", "ticket_id", "maintenancetickets"), ("workcompliance", "technician_id", "employees"),
        ("workinstructions", "line_id", "lines"), ("operatorcompliance", "run_id", "productionruns"), ("operatorcompliance", "operator_id", "employees"),
        ("operatorcompliance", "instruction_id", "workinstructions")
    ];

    public static object Json(PlantModel model)
    {
        var tables = PlantSchema.TablesFor(model);
        var purposes = tables.Select(t => t.Purpose).ToHashSet();
        return new
        {
            simulation = model.Simulation,
            database = new { host = "flow-data-sim-pg", port = 5432, database = "flow_data_sim", user = "flowsim_reader",
                password = "See ~/flow-data-simulator/config/postgres.env (FLOWSIM_READER_PASSWORD). Never copy it into specs or tickets." },
            watermark = "row_version (bigint, strictly increasing across inserts and updates of every simulation table)",
            tables = tables.Select(t => new
            {
                name = PlantSchema.PhysicalName(model, t.Purpose), purpose = t.Purpose, t.Description, key = t.Key,
                columns = t.Columns.Concat(PlantSchema.StandardColumns).Select(c => new { c.Name, c.Type, c.Nullable }),
                references = Relationships.Where(r => r.Table == t.Purpose && purposes.Contains(r.References))
                    .Select(r => new { column = r.Column, table = PlantSchema.PhysicalName(model, r.References) }),
                incrementalQuery = IncrementalQuery(model, t)
            }),
            atlas = AtlasStarter(model)
        };
    }

    private static string IncrementalQuery(PlantModel model, PlantTable table) =>
        $"SELECT {string.Join(", ", table.Columns.Select(c => c.Name))}, row_version, modified_utc, is_deleted FROM {PlantSchema.PhysicalName(model, table.Purpose)} " +
        "WHERE row_version > @watermark ORDER BY row_version LIMIT 1000";

    private static object AtlasStarter(PlantModel model)
    {
        var tags = new PlantSignals(new PlantRun(model, model.StartUtc, model.StartUtc, [], [], [])).Tags;
        string Table(string purpose) => PlantSchema.PhysicalName(model, purpose);
        return new
        {
            note = "Starter for Atlas modeling: instances for the asset hierarchy and sensors; transactional records as query-backed properties. Not a compiled Atlas payload.",
            instances = new object[] { new { type = "Site", id = model.Simulation, name = model.Simulation } }
                .Concat(model.Lines.Select(l => (object)new { type = "Line", id = l.Id, name = l.Name, parent = model.Simulation }))
                .Concat(model.Lines.SelectMany(l => l.Equipment.Select(e => (object)new { type = "Equipment", id = $"{l.Id}.{e.Id}", name = e.Name, equipmentClass = e.Class, parent = l.Id })))
                .Concat(tags.Select(t => (object)new
                {
                    type = "Sensor", id = t.Name, measure = t.Measure, valueType = t.ValueType,
                    parent = t.Equipment is { } e ? $"{t.Line}.{e}" : t.Line.Length > 0 ? t.Line : model.Simulation,
                    historianTag = t.Name
                }))
                .Concat(model.Skus.Select(s => (object)new { type = "Product", id = s.Code, name = s.Description }))
                .Concat(model.Operators.Concat(model.Technicians).Select(p => (object)new { type = "Person", id = p.Id, name = p.Name, shift = p.Shift, skills = p.Skills })),
            queryProperties = new object[]
            {
                new { on = "Equipment", name = "OpenMaintenanceTickets", query = $"SELECT ticket_id, ticket_type, priority, status, created_utc, technician_id FROM {Table("maintenancetickets")} WHERE equipment_id = [EquipmentId] AND status <> 'Complete' AND NOT is_deleted ORDER BY created_utc" },
                new { on = "Equipment", name = "MaintenanceHistory", query = $"SELECT ticket_id, failure_code, priority, created_utc, completed_utc FROM {Table("maintenancetickets")} WHERE equipment_id = [EquipmentId] AND NOT is_deleted ORDER BY created_utc DESC LIMIT 50" },
                new { on = "Equipment", name = "DowntimeEvents", query = $"SELECT downtime_id, category, reason, start_utc, end_utc, duration_minutes FROM {Table("downtimeevents")} WHERE equipment_id = [EquipmentId] AND NOT is_deleted ORDER BY start_utc DESC LIMIT 50" },
                new { on = "Line", name = "CurrentProductionOrder", query = $"SELECT production_order, sku, quantity_cases, started_utc, due_utc FROM {Table("productionorders")} WHERE line_id = [LineId] AND status = 'Running' AND NOT is_deleted" },
                new { on = "Line", name = "ProductionRuns", query = $"SELECT run_id, production_order, sku, operator_id, start_utc, end_utc, good_cases, reject_cases FROM {Table("productionruns")} WHERE line_id = [LineId] AND NOT is_deleted ORDER BY start_utc DESC LIMIT 50" },
                new { on = "Person", name = "AssignedTickets", query = $"SELECT ticket_id, equipment_id, status, assigned_utc FROM {Table("maintenancetickets")} WHERE technician_id = [PersonId] AND status <> 'Complete' AND NOT is_deleted" }
            }.Concat(model.Quality is null ? [] : new object[]
            {
                new { on = "Line", name = "QualityHolds", query = $"SELECT h.hold_id, h.lot_id, h.reason, h.created_utc, h.released_utc FROM {Table("qualityholds")} h JOIN {Table("lots")} l ON l.lot_id = h.lot_id WHERE l.line_id = [LineId] AND NOT h.is_deleted ORDER BY h.created_utc DESC LIMIT 50" }
            }).Concat(model.Shipping is null ? [] : new object[]
            {
                new { on = "Product", name = "OpenShipments", query = $"SELECT s.shipment_id, s.sales_order, s.carrier, s.quantity_cases, s.planned_utc FROM {Table("shipments")} s JOIN {Table("salesorders")} o ON o.sales_order = s.sales_order WHERE o.sku = [ProductId] AND s.status = 'Scheduled' AND NOT s.is_deleted" }
            }).Concat(model.Maintenance.Certification is null ? [] : new object[]
            {
                new { on = "Person", name = "ExpiredCertifications", query = $"SELECT record_id, skill, issued_utc, expires_utc, status FROM {Table("trainingrecords")} WHERE employee_id = [PersonId] AND status = 'Expired' AND NOT is_deleted ORDER BY expires_utc DESC" },
                new { on = "Equipment", name = "UncertifiedWork", query = $"SELECT c.ticket_id, c.technician_id, c.skill, c.reason, c.checked_utc, t.failure_code, t.completed_utc FROM {Table("workcompliance")} c JOIN {Table("maintenancetickets")} t ON t.ticket_id = c.ticket_id WHERE t.equipment_id = [EquipmentId] AND NOT c.certified AND NOT c.is_deleted ORDER BY c.checked_utc DESC" }
            }).Concat(model.Maintenance.Certification?.Operators is null ? [] : new object[]
            {
                new { on = "Line", name = "UncertifiedRuns", query = $"SELECT c.run_id, c.operator_id, c.instruction_id, c.reason, r.sku, r.start_utc, r.end_utc, r.good_cases, r.reject_cases FROM {Table("operatorcompliance")} c JOIN {Table("productionruns")} r ON r.run_id = c.run_id WHERE r.line_id = [LineId] AND NOT c.certified AND NOT c.is_deleted ORDER BY r.start_utc DESC" }
            })
        };
    }

    public static string Markdown(PlantModel model)
    {
        var text = new StringBuilder();
        var tables = PlantSchema.TablesFor(model);
        var purposes = tables.Select(t => t.Purpose).ToHashSet();
        text.AppendLine($"# {model.Simulation}: Connect mapping spec").AppendLine();
        text.AppendLine("Generated by `plant connect-spec`. Read-only Postgres source for Timebase Connect.").AppendLine();
        text.AppendLine("| Setting | Value |").AppendLine("| --- | --- |");
        text.AppendLine("| Host | `flow-data-sim-pg` (Timebase Docker network), or `127.0.0.1:5435` from the Mac |");
        text.AppendLine("| Database | `flow_data_sim` |");
        text.AppendLine("| User | `flowsim_reader` (read-only) |");
        text.AppendLine("| Password | In `~/flow-data-simulator/config/postgres.env` (`FLOWSIM_READER_PASSWORD`). Never paste it into specs or tickets. |");
        text.AppendLine($"| Tables | {tables.Count}, all named `{model.TablePrefix}<purpose>` |").AppendLine();
        text.AppendLine("## Polling rules").AppendLine();
        text.AppendLine("- **Watermark:** `row_version` is a bigint from one shared sequence, stamped on every insert and update. Poll `WHERE row_version > @watermark ORDER BY row_version`.");
        text.AppendLine("- **Keys:** each table's natural key (first column) is its primary key. Updates change the row in place (for example a ticket moving Open → Complete), so map by key.");
        text.AppendLine("- **`modified_utc`** is the simulated change time, for display only. Don't use it as a watermark.");
        text.AppendLine("- **Soft deletes:** `is_deleted` and `deleted_utc` exist for the connector contract. The simulator never deletes rows.");
        text.AppendLine("- **Live data:** rows appear when their simulated time arrives, so the latest row is about now and never in the future.").AppendLine();
        foreach (var table in tables)
        {
            text.AppendLine($"## `{PlantSchema.PhysicalName(model, table.Purpose)}`").AppendLine();
            text.AppendLine($"{table.Description}. Key: `{table.Key}`.").AppendLine();
            text.AppendLine("| Column | Type | Null |").AppendLine("| --- | --- | --- |");
            foreach (var column in table.Columns.Concat(PlantSchema.StandardColumns))
                text.AppendLine($"| `{column.Name}` | {column.Type} | {(column.Nullable ? "yes" : "no")} |");
            var references = Relationships.Where(r => r.Table == table.Purpose && purposes.Contains(r.References)).ToList();
            if (references.Count > 0)
            {
                text.AppendLine().Append("References: ");
                text.AppendLine(string.Join(", ", references.Select(r => $"`{r.Column}` → `{PlantSchema.PhysicalName(model, r.References)}`")));
            }
            text.AppendLine().AppendLine("```sql").AppendLine(IncrementalQuery(model, table)).AppendLine("```").AppendLine();
        }
        return text.ToString();
    }
}
