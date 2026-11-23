<plan title="Phase 1: Tables API">

<overview>
Phase 1 of the Plant browser plan (`plan.md`). Adds a read-only `PlantBrowser` service
and endpoints for the plant Summary, the plant's tables with row counts, and paged rows
with sort, search and a deleted-rows toggle. Narrows `/api/plants` to plants this page
created.
</overview>

<context>
Phase 0 is committed: the Timebase look is on `web-timebase-look` and 1,137 tests pass.
Run everything from the worktree `wt-timebase-look`. Tests that need Postgres read
`FLOWSIM_TEST_PG` (build it from `~/flow-data-simulator/config/datasim.env` with
`Host=127.0.0.1;Port=5435`; never print it).

Rule for the whole browser: a plant is browsable only when
`services.Interviews.ApprovedSimulations()` contains it **and** `services.Plants.Registration(name)`
exists. `WebPlantDirectory` already applies this rule for the interviewer; extract the
check into one helper both use, so the interviewer and the browser can never disagree. Anything else answers 404 `browse.not_found` with the same message, so the page
never reveals other simulations.
</context>

<changes-required>

<change component="Read-only query on PlantStore">
**File**: `src/IndustrialDataSim.Runtime/PlantStore.cs`
**Changes**: Add one method that runs a SELECT in a read-only transaction with a statement
timeout, returning column names and rows as CLR values. The caller (PlantBrowser) builds
the SQL only from schema identifiers; values always travel as parameters.

```csharp
/// <summary>
/// Runs one SELECT in a READ ONLY transaction with a 5-second statement timeout
/// and returns at most maxRows rows. Used by the page's plant browser; the SQL is
/// built from schema identifiers only and every value is a parameter.
/// </summary>
public (IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows) ReadOnlyQuery(
    string sql, IReadOnlyList<NpgsqlParameter> parameters, int maxRows)
{
    using var connection = Open();
    using var transaction = connection.BeginTransaction();
    Execute(connection, transaction, "SET TRANSACTION READ ONLY");
    Execute(connection, transaction, "SET LOCAL statement_timeout = '5s'");
    // ExecuteReader, read up to maxRows, map DBNull → null, DateTime → DateTimeOffset (UTC).
    // On 57014 (query_canceled) throw RuntimeFailure("browse.query_timeout",
    //   "The table query took longer than 5 seconds. Narrow the search or try again.").
    // Roll back (read-only: nothing to keep).
}
```
</change>

<change component="PlantBrowser service">
**File**: `src/IndustrialDataSim.Web/PlantBrowser.cs` (new)
**Changes**: Owns which plants, tables and columns can be read, and the limits.

```csharp
public sealed class PlantBrowser(WebServices services)
{
    public const int PageSize = 200;
    public const int MaxSearchLength = 100;
    public const int MaxOffset = 100_000;      // page × PageSize; deeper paging is refused
    public const int MaxCellLength = 2_000;    // longer text is cut with "…"

    /// <summary>The registration and loaded model of a plant this page created, or null.</summary>
    public (PlantRegistration Registration, PlantModel Model)? Find(string simulation);

    public object Summary(string simulation);           // see shape below
    public IReadOnlyList<object> Tables(string simulation);
    public object Rows(string simulation, string table, string? sort, string? dir, string? q, bool deleted, int page);
}
```

Summary shape (`GET /api/browse/{name}`):

```json
{
  "simulation": "PlanTestX", "tablePrefix": "plantestx_",
  "dataset": "PlanTestX", "datasetCreated": true,
  "window": { "startUtc": "…", "anchorUtc": "…" },
  "delivery": { "appliedSequence": 1234, "tagCursorUtc": "…", "snapshotUtc": "…" },
  "hosting": { "state": "Live", "sinceUtc": "…", "message": "…" },
  "lines": [ { "id": "L1", "name": "…", "kind": "packaging", "equipment": [ { "id": "LB-202", "name": "…", "class": "…" } ] } ],
  "incidents": [ { "failureMode": "LB202-JAM", "atUtc": "…", "note": "…", "shift": null, "line": "L1", "equipment": "LB-202" } ],
  "drifts": [ … ],
  "storylines": [ { "id": "…", "title": "…" } ],
  "modules": { "quality": true, "shipping": false, "certification": false, "batch": false },
  "counts": { "tables": 23, "tags": 39 },
  "downloads": { "connectSpec": "/api/plants/PlanTestX/connect-spec", "atlasStarter": "…", "demoGuide": "…",
                 "gagePrompts": "/api/plants/PlanTestX/gage/prompts", "gageConnect": "…", "gageAtlas": "…" }
}
```

- `hosting` comes from `PlantHostingService.Status(name)` with the same fallbacks as `/api/plants`.
- `storylines` come from the approved interview (`FindInterview(services, name)` in `WebApp.cs`, made
  internal or moved to a shared helper) and are omitted when the interview has none.
- Incidents come from `PlantIncident(FailureMode, AtUtc, Note, Shift)` (`PlantModel.cs:216`); `line` and `equipment` are
  resolved from the failure mode's equipment in the model (omit them if the model cannot say). Drifts come from
  `PlantDrift` (`PlantModel.cs:106`) as they are.

Tables (`GET /api/browse/{name}/tables`): one entry per `PlantSchema.TablesFor(model)`:

```json
[ { "name": "plantestx_maintenancetickets", "purpose": "maintenancetickets", "description": "…",
    "rows": 120, "deletedRows": 0,
    "columns": [ { "name": "ticket_id", "type": "text" }, …, { "name": "row_version", "type": "bigint" } ] } ]
```

Counts: one query per plant,
`SELECT '<purpose>' AS t, count(*), count(*) FILTER (WHERE is_deleted) FROM <table> UNION ALL …`
built from schema identifiers (quoted with `"…"`; physical names are already lowercase).

Rows (`GET /api/browse/{name}/tables/{table}/rows?sort=&dir=&q=&deleted=&page=`):

- `table` must equal one physical name from `TablesFor(model)`; else 404 `browse.unknown_table`.
- `sort` must be one of that table's columns (incl. standard columns); default `row_version`;
  else 400 `browse.bad_sort` naming the allowed columns. `dir` is `asc` or `desc` (default `desc`).
- `q`: trimmed, at most 100 characters (else 400 `browse.bad_search`); matched with
  `ILIKE @pattern ESCAPE '\'` (escape `\`, `%`, `_`) against every column whose schema type is
  text-like, cast `::text` for others is **not** done (keeps it index-friendly and predictable).
- `deleted=false` (default) adds `NOT is_deleted`.
- `page` ≥ 1; `(page-1)*200` ≤ 100,000 else 400 `browse.bad_page`.
- SQL: `SELECT <cols> FROM <table> WHERE … ORDER BY <sort> <dir> NULLS LAST, row_version DESC LIMIT 200 OFFSET @offset`,
  plus `SELECT count(*) FROM <table> WHERE …` for `total`.
- Response:

```json
{ "table": "plantestx_maintenancetickets", "columns": ["ticket_id", …],
  "rows": [ ["MT-000001", "…", 3, false, "2026-11-22T10:00:00Z"] ],
  "page": 1, "pageSize": 200, "total": 120, "sort": "row_version", "dir": "desc", "q": null, "deleted": false }
```

Values: timestamps as ISO 8601 UTC, numbers as numbers, booleans, null; text longer than
2,000 characters is cut and ends with "…".
</change>

<change component="Endpoints and /api/plants">
**File**: `src/IndustrialDataSim.Web/WebApp.cs`
**Changes**:
- Add `MapBrowse(app, services, hosting)` with the three GET endpoints above. Each wraps
  `RuntimeFailure` with `Failure(failure, 503)` like the plants endpoints, and logs nothing per
  request (reads are not events).
- `/api/plants`: filter `registered` to `owned.Contains(r.Simulation)`. Remove the `managed`
  false branch and the "running elsewhere / not hosted" fallback wording that only applied to
  plants created elsewhere; keep `RunningElsewhere` for a page-created plant another process
  happens to host.
- Add the Postgres read to `WebServices` only if needed; `PlantBrowser` can be created per
  request from `services` (it is stateless).
</change>

<change component="Tests">
**File**: `tests/IndustrialDataSim.Tests/BrowseTests.cs` (new); `tests/IndustrialDataSim.Tests/WebTests.cs`
**Changes**: Using the existing `Start(...)` harness with `FLOWSIM_TEST_PG` (skip like the
other Postgres tests when it is unset). Seed by approving a scripted interview (as
`ApproveCreatesThePlantsOwnDatasetAndRemoveDeletesItOnlyWhenAsked` does) with `hostPlants:false`,
then apply a short run of changes with `PlantStore.ApplyChanges` so tables have rows. Clean up by
removing via the API.

Tests:
- `TheBrowserListsOnlyPlantsThisPageCreated`: admit a second plant directly with `PlantStore.Admit`
  (not approved here) → absent from `/api/plants`; `/api/browse/{it}` and its tables/rows → 404
  `browse.not_found`, same message as a name that does not exist.
- `SummaryDescribesThePlantFromItsModelAndRegistration`: lines, counts.tables = TablesFor count,
  counts.tags = PlantSignals tag count, dataset, downloads.
- `TablesListEveryPlantTableWithRowCounts`: counts equal `SELECT count(*)` on each table.
- `RowsPageNewestFirstAndSortSearchAndDeletedWork`: default order is row_version desc; `sort`
  asc/desc; `q` with `%` and `_` is matched literally; `deleted=true` includes a soft-deleted row;
  page 2 continues page 1 without overlap.
- `RowsRefuseAnotherPlantsTableUnknownColumnsAndDeepPages`: other plant's physical table → 404
  `browse.unknown_table`; `sort=pg_sleep` → 400 `browse.bad_sort`; `q` of 101 chars → 400;
  `page=502` → 400 `browse.bad_page`.
- `ReadOnlyQueryCannotWrite`: `PlantStore.ReadOnlyQuery("DELETE FROM …")` throws (25006
  read_only_sql_transaction) and the row count is unchanged.
- Update any existing test that expected a plant not approved here in `/api/plants`.
</change>

</changes-required>

<success-criteria>

<automated-verification>
- [x] Builds: `dotnet build -c Release`
- [x] Full suite passes with `FLOWSIM_TEST_PG` set: `dotnet test -c Release`
- [x] New tests run (not skipped): `dotnet test -c Release --filter FullyQualifiedName~BrowseTests` reports ≥ 6 passed (implemented as 4 tests covering the same cases: TheBrowserListsOnlyPlantsThisPageBuilt, SummaryAndTablesDescribeThePlantFromItsModelAndPostgres, RowsPageNewestFirstAndSortSearchDeletedAndRefusalsWork, ReadOnlyQueryCannotWrite)
</automated-verification>

<end-to-end-verification>
Against an in-process app is covered by the tests; the container check comes in Phase 3.
- [x] `git grep -n "ApprovedSimulations" src/IndustrialDataSim.Web` shows the browser and `/api/plants` both use it.
- [x] No endpoint added in this phase accepts POST, PUT or DELETE (`grep -n "MapPost\|MapDelete" src/IndustrialDataSim.Web/WebApp.cs` unchanged except existing ones).
- [x] Reviewing `PlantBrowser.cs`: every identifier interpolated into SQL comes from `PlantSchema`; every browser value is a parameter.
</end-to-end-verification>

</success-criteria>

<post-verification>
Commit: "Plant browser API: Summary, tables and paged rows for plants this page created".
</post-verification>

</plan>
