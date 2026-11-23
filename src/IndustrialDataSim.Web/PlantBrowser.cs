using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Plant;
using IndustrialDataSim.Runtime;
using Npgsql;

namespace IndustrialDataSim.Web;

/// <summary>
/// The plants this page built: approved from one of its interviews and still
/// registered in flow_data_sim. The Plants list, the interviewer's plant tools
/// and the plant browser all use this one rule, so they never disagree.
/// BluebirdBev and plants made by other tools are never included.
/// </summary>
internal static class PagePlants
{
    public static IReadOnlyList<PlantRegistration> List(WebServices services)
    {
        var owned = services.Interviews.ApprovedSimulations();
        return services.Plants.List().Where(r => owned.Contains(r.Simulation)).ToList();
    }

    public static PlantRegistration? Find(WebServices services, string simulation) =>
        services.Interviews.ApprovedSimulations().Contains(simulation) ? services.Plants.Registration(simulation) : null;

    /// <summary>The hosting status shown for a page-built plant, with the fallbacks the Plants list uses.</summary>
    public static PlantHostingStatus Status(WebServices services, PlantHostingService hosting, string simulation) =>
        hosting.Status(simulation) ?? (services.Plants.IsHostRunning(simulation)
            ? new PlantHostingStatus(HostingState.RunningElsewhere, DateTimeOffset.UtcNow, Message: "Another process is delivering this plant. This container does not touch it.")
            : new PlantHostingStatus(HostingState.Stopped, DateTimeOffset.UtcNow, Message: "Not running. Start it to continue where it left off."));
}

/// <summary>A browser request the page must correct; the endpoint answers with this status, code and message.</summary>
public sealed class BrowseRefusal(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>
/// Read-only views of one page-built plant for the page's plant browser:
/// a summary from its model and registration, its own tables with row counts,
/// and pages of rows. Every query runs in a read-only transaction with a
/// 5-second timeout; table and column names come only from PlantSchema, and
/// every value from the browser is a parameter.
/// </summary>
public sealed class PlantBrowser(WebServices services, PlantHostingService hosting)
{
    public const int PageSize = 200;
    public const int MaxSearchLength = 100;
    /// <summary>Deepest offset served ((page − 1) × PageSize); deeper paging is refused, search narrows instead.</summary>
    public const int MaxOffset = 100_000;
    /// <summary>Longer text is cut and ends with "…".</summary>
    public const int MaxCellLength = 2_000;

    public static int MaxPage => MaxOffset / PageSize + 1;

    private static BrowseRefusal NotFound() =>
        new(404, "browse.not_found", "No plant with this name was built on this page. Pick one from the Plants list.");

    /// <summary>The registration and loaded model of a plant this page built.</summary>
    public (PlantRegistration Registration, PlantModel Model) Find(string simulation)
    {
        var registration = PagePlants.Find(services, simulation) ?? throw NotFound();
        var loaded = PlantModelLoader.Load(registration.ModelJson, registration.AnchorUtc);
        if (!loaded.IsValid)
            throw new BrowseRefusal(409, "plant.stored_model_invalid", "The stored model no longer validates with this version of the simulator.");
        return (registration, loaded.Model!);
    }

    public object Summary(string simulation)
    {
        var (registration, model) = Find(simulation);
        var entry = services.Registry.Find(simulation);
        string? dataset = entry?.Dataset ?? services.Dataset;
        var failureModes = model.Maintenance.FailureModes.ToDictionary(f => f.Code, StringComparer.Ordinal);
        string Base(string path) => $"/api/plants/{Uri.EscapeDataString(simulation)}/{path}";
        return new
        {
            simulation = model.Simulation,
            tablePrefix = model.TablePrefix,
            tagRoot = model.TagRoot,
            dataset,
            datasetCreated = entry?.DatasetCreated == true,
            window = new { startUtc = registration.StartUtc, anchorUtc = registration.AnchorUtc },
            delivery = new
            {
                appliedSequence = registration.AppliedSequence,
                tagCursorUtc = registration.TagCursorUtc,
                snapshotUtc = services.Plants.SnapshotUtc(simulation)
            },
            hosting = PagePlants.Status(services, hosting, simulation),
            lines = model.Lines.Select(l => new
            {
                id = l.Id, name = l.Name, kind = l.Kind,
                equipment = l.Equipment.Select(e => new { id = e.Id, name = e.Name, @class = e.Class })
            }),
            incidents = model.Incidents.Select(i =>
            {
                failureModes.TryGetValue(i.FailureMode, out var mode);
                return new { failureMode = i.FailureMode, atUtc = i.AtUtc, note = i.Note, shift = i.Shift, line = mode?.Line, equipment = mode?.Equipment };
            }),
            drifts = model.Drifts.Select(d => new { line = d.Line, recipe = d.Recipe, phase = d.Phase, cause = d.Cause, fromUtc = d.FromUtc, untilUtc = d.UntilUtc }),
            storylines = Storylines(simulation),
            modules = new
            {
                quality = model.Quality is not null,
                shipping = model.Shipping is not null,
                certification = model.Maintenance.Certification is not null,
                batch = model.Lines.Any(l => l.Kind == "batch")
            },
            counts = new
            {
                tables = PlantSchema.TablesFor(model).Count,
                tags = new PlantSignals(new PlantRun(model, model.StartUtc, model.StartUtc, [], [], [])).Tags.Count
            },
            downloads = new
            {
                connectSpec = Base("connect-spec"),
                atlasStarter = Base("atlas-starter"),
                demoGuide = Base("demo-guide"),
                gagePrompts = Base("gage/prompts"),
                gageConnect = Base("gage/connect"),
                gageAtlas = Base("gage/atlas")
            }
        };
    }

    private List<object> Storylines(string simulation)
    {
        var interview = WebApp.FindInterview(services, simulation);
        if (interview?.NeedsJson is null) return [];
        var needs = JsonNode.Parse(interview.NeedsJson) as JsonObject;
        var result = new List<object>();
        foreach (var id in (needs?["chosenStorylines"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>())
            if (services.Library.Storylines.Find(id) is { } storyline)
                result.Add(new { id = storyline.Id, title = storyline.Title });
        return result;
    }

    /// <summary>Every table of the plant with its columns and row counts (one query).</summary>
    public IReadOnlyList<object> Tables(string simulation)
    {
        var (_, model) = Find(simulation);
        var tables = PlantSchema.TablesFor(model);
        // Identifiers come from PlantSchema; the index is a literal integer.
        string sql = string.Join(" UNION ALL ", tables.Select((t, i) =>
            $"SELECT {i} AS i, count(*) AS total, count(*) FILTER (WHERE is_deleted) AS deleted FROM {Quote(PlantSchema.PhysicalName(model, t.Purpose))}"));
        var (_, rows) = services.Plants.ReadOnlyQuery(sql, [], tables.Count);
        var counts = rows.ToDictionary(r => Convert.ToInt32(r[0]), r => (Total: Convert.ToInt64(r[1]), Deleted: Convert.ToInt64(r[2])));
        return tables.Select((t, i) => (object)new
        {
            name = PlantSchema.PhysicalName(model, t.Purpose),
            purpose = t.Purpose,
            description = t.Description,
            rows = counts.TryGetValue(i, out var c) ? c.Total : 0,
            deletedRows = counts.TryGetValue(i, out var d) ? d.Deleted : 0,
            columns = Columns(t).Select(col => new { name = col.Name, type = col.Type })
        }).ToList();
    }

    private static IEnumerable<PlantColumn> Columns(PlantTable table) => table.Columns.Concat(PlantSchema.StandardColumns);

    /// <summary>One page of a table's rows, newest first by default.</summary>
    public object Rows(string simulation, string table, string? sort, string? dir, string? q, bool deleted, int page)
    {
        var (_, model) = Find(simulation);
        var plantTable = PlantSchema.TablesFor(model).FirstOrDefault(t => PlantSchema.PhysicalName(model, t.Purpose) == table)
            ?? throw new BrowseRefusal(404, "browse.unknown_table", $"{simulation} has no table named {table}. Pick one from its table list.");
        var columns = Columns(plantTable).ToList();

        string sortColumn = string.IsNullOrEmpty(sort) ? "row_version" : sort;
        if (columns.All(c => c.Name != sortColumn))
            throw new BrowseRefusal(400, "browse.bad_sort", $"Sort by one of the table's columns: {string.Join(", ", columns.Select(c => c.Name))}.");
        string direction = (dir ?? "desc").ToLowerInvariant();
        if (direction is not ("asc" or "desc"))
            throw new BrowseRefusal(400, "browse.bad_sort", "The sort direction is asc or desc.");

        string? search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        if (search is { Length: > MaxSearchLength })
            throw new BrowseRefusal(400, "browse.bad_search", $"Search for at most {MaxSearchLength} characters.");
        if (page < 1 || (long)(page - 1) * PageSize > MaxOffset)
            throw new BrowseRefusal(400, "browse.bad_page", $"Pages run from 1 to {MaxPage}. Narrow the rows with the search box to reach older ones.");

        var where = new List<string>();
        if (!deleted) where.Add("NOT is_deleted");
        var textColumns = columns.Where(c => c.Type == "text").ToList();
        if (search is not null)
            where.Add(textColumns.Count == 0 ? "false" : "(" + string.Join(" OR ", textColumns.Select(c => $"{Quote(c.Name)} ILIKE @pattern ESCAPE '\\'")) + ")");
        string filter = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
        string pattern = search is null ? "" : "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        List<NpgsqlParameter> Parameters(bool withOffset)
        {
            var list = new List<NpgsqlParameter>();
            if (search is not null) list.Add(new NpgsqlParameter("pattern", pattern));
            if (withOffset) list.Add(new NpgsqlParameter("offset", (page - 1) * PageSize));
            return list;
        }

        string from = Quote(table);
        string select = $"SELECT {string.Join(", ", columns.Select(c => Quote(c.Name)))} FROM {from}{filter} " +
                        $"ORDER BY {Quote(sortColumn)} {direction.ToUpperInvariant()} NULLS LAST, row_version DESC LIMIT {PageSize} OFFSET @offset";
        var (names, rows) = services.Plants.ReadOnlyQuery(select, Parameters(withOffset: true), PageSize);
        var (_, count) = services.Plants.ReadOnlyQuery($"SELECT count(*) FROM {from}{filter}", Parameters(withOffset: false), 1);

        return new
        {
            table,
            columns = names,
            rows = rows.Select(r => r.Select(Cell).ToArray()),
            page,
            pageSize = PageSize,
            total = Convert.ToInt64(count[0][0]),
            sort = sortColumn,
            dir = direction,
            q = search,
            deleted
        };
    }

    private static object? Cell(object? value) => value switch
    {
        string text when text.Length > MaxCellLength => text[..MaxCellLength] + "…",
        DateTimeOffset time => time.ToUniversalTime(),
        _ => value
    };

    /// <summary>Names come from PlantSchema and validated prefixes; quoting is defense in depth.</summary>
    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
