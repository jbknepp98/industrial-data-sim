<plan title="Plant browser in the web page (2.0.0-rc.3) Implementation Plan">

<overview>
The Plants view of the web page (`flow-data-simulator-web`) becomes a browser for the
simulations this tool created: a list on the left, and for the selected plant a
read-only detail view with three sections:

- **Summary**: status, simulated window, delivery progress, Dataset, lines,
  storylines and planted incidents, downloads, and the Stop / Start / Remove actions.
- **Time series**: one master start/end selector for the plant; any number of trend
  windows, each with up to 4 tags picked from a line → machine tree; every window
  follows the master range; the server thins each tag to at most 1,000 points.
- **Tables**: the plant's own Postgres tables with row counts; rows 200 per page,
  newest first, click-to-sort on any column, one search box, "show deleted rows".

It ships together with the other work already on this branch (uncommitted when this
plan was revised): the Timebase Plant Simulator page and two-step interview, attached
files, existing-plant handling with rebuild in place, and the Gage kit, as
**2.0.0-rc.3**. Owner decisions, November 22, 2026:

- Replace the Plants view (not a separate Browse item).
- Show only environments this tool created. Plants delivered by other tools or
  containers (BluebirdBev today), other Datasets, Atlas domains and other tables in
  `flow_data_sim` are never listed or linked.
- Trends: free start/end range, up to 4 tags per window, multiple windows bound to one
  master date/time selector, 1,000-point thinning.
- Tables: one search box, click-to-sort, deleted-rows toggle.
</overview>

<current-state-analysis>
The page lists every registered simulation with one status row and three downloads.
Nothing reads rows or tag values back.

<key-discoveries>
- `PlantStore.List()` (`src/IndustrialDataSim.Runtime/PlantStore.cs:105`) returns every
  registration with its stored model JSON, anchor, start and delivery progress.
- "Created by this tool" already has a definition, now in one place for the
  interviewer: `WebPlantDirectory` (`src/IndustrialDataSim.Web/WebPlantDirectory.cs`)
  lists plants approved from one of the page's interviews and present in the plant
  store. The browser reuses that filter (or a shared helper extracted from it) instead of
  a second copy. Before that, `Ownership()`
  (`src/IndustrialDataSim.Web/WebApp.cs:484`) uses
  `services.Interviews.ApprovedSimulations()`; the page registry
  (`WebPlantRegistry.cs:16`) records each such plant's Dataset (rc.1 plants: null,
  meaning `TIMEBASE_DATASET`).
- Tables come from the model: `PlantSchema.TablesFor(model)` and
  `PlantSchema.PhysicalName(model, purpose)` (`src/IndustrialDataSim.Core/Plant/PlantSchema.cs:123,148`),
  plus `PlantSchema.StandardColumns` (`row_version`, `modified_utc`, `is_deleted`).
  Column names come from the schema only, so sorting and searching never put browser
  text into SQL identifiers.
- Tags come from the model: `new PlantSignals(new PlantRun(model, model.StartUtc, model.StartUtc, [], [], [])).Tags`
  (as in `PlantConnectSpec.cs:70`); each `PlantTag(Name, ValueType, Measure, Line, Equipment)`
  carries its line and machine (`PlantSignals.cs:4`).
- The runtime only writes to Historian. Reading is documented in
  `docs/timebase-api-findings.md:143`:
  `GET /api/datasets/{dataset}/data?tagname=…&tagname=…&start=…&end=…` returns
  `{s, e, tl:[{t:{…}, d:[{t, v, q}]}]}`. It may include a leading boundary point
  before `start`, and an empty range can return a `0001-01-01T00:00:00Z` placeholder
  with quality 0 and no value. Both must be filtered.
- `HistorianClient` patterns to follow: deadlines with `CancellationTokenSource.CancelAfter`,
  `HttpCompletionOption.ResponseHeadersRead`, bounded `ReadBytes`, error bodies never
  echoed (`HistorianClient.cs:111-155`).
- The download endpoints and `Download()` helper in `WebApp.cs` already load and
  validate the stored model; the browser reuses that loading. Line numbers in this plan
  predate the branch's later changes: look the symbols up.
- The page already has Historian's layout: the Plants list is a Datasets-style card
  (`plantRow`, `expandedPlants` in `app.js`), **New plant** in the top strip is the only
  way to start a plant (no button in the Plants toolbar), and startup opens Plants when
  the page has built a plant. The browser keeps all of that.
- Web tests build the app in-process with fakes (`tests/IndustrialDataSim.Tests/WebTests.cs:106`);
  Postgres tests use `FLOWSIM_TEST_PG`. `Historian` is a `Func<string, HistorianClient?>`
  in `WebServices` (`WebServices.cs:18`), null in tests.
- The shared preview renderer draws line charts in SVG with `vector-effect: non-scaling-stroke`
  (`tools/plant-preview/plant-preview.js:216`); the trend charts follow its style and
  colors (`--accent`, `--run`, …).
</key-discoveries>
</current-state-analysis>

<desired-end-state>
- Opening Plants shows only plants this page approved. Selecting one shows Summary,
  Time series and Tables, deep-linkable as `#plant=<name>&section=<summary|series|tables>`.
- Every browser endpoint is read-only, returns 404 `browse.not_found` for a plant the
  page did not create (without saying whether it exists), and is bounded (200 rows,
  4 tags, 1,000 points per tag, 31 days).
- 2.0.0-rc.3 is tagged after a passing CI run; the web container runs the CI image; a
  live check with a new `PlanTest` plant passed (acceptance gate P27, plus P23–P26 for
  the rest of the branch).
</desired-end-state>

<out-of-scope>
- Plants created by other tools or containers, other Datasets, Atlas domains, Connect setup.
- Exporting data, editing anything, a SQL box.
- Live auto-refresh of trends (a Refresh button re-reads).
- Historian-side aggregation (the server thins after reading; see Phase 2).
- Changes to the CLI, the engine or the session simulator.
</out-of-scope>

<implementation-approach>
A new read-only `PlantBrowser` service in the Web project owns the rules (which plants,
which tables, which tags, limits). It reads Postgres through a new read-only query
method on `PlantStore` and Historian through a new `HistorianClient.ReadTagData`.
Endpoints sit under `/api/browse/…` so the existing `/api/plants` contract for Stop,
Start and Remove stays as it is (its list is narrowed to plants this page created).
The page keeps its no-framework style: `app.js` gains a `browse` module that renders
the list and detail, and draws trends as SVG.
</implementation-approach>

<phases>

<phase number="0" name="Commit the work already on the branch" detail-plan="(inline)">
<summary>Commit what is in the worktree as separate commits on `web-timebase-look`, after
re-running the build and the test suite: the Timebase Plant Simulator page and two-step
interview; attached files; existing plants and rebuild in place; the Gage kit; their
documentation (engineering docs, specification, acceptance P23–P26, skill in the
repository only).</summary>
<files-affected>Already changed; no new edits.</files-affected>
<depends-on>None</depends-on>
<verification-summary>
- `dotnet build -c Release` and `dotnet test -c Release` pass with `FLOWSIM_TEST_PG` (1,151 tests when last run).
</verification-summary>
</phase>

<phase number="1" name="Tables API" detail-plan="phase-1-plan.md">
<summary>`PlantBrowser` service and read-only endpoints: owned plant list, Summary,
table list with counts, paged rows with sort, search and deleted toggle. `/api/plants`
narrows to plants this page created.</summary>
<files-affected>~6: new `PlantBrowser.cs`, `PlantStore.cs` (read-only query), `WebApp.cs`, `WebServices.cs`, new `BrowseTests.cs`, `WebTests.cs`</files-affected>
<depends-on>Phase 0</depends-on>
<verification-summary>
- Build and full test suite pass against `FLOWSIM_TEST_PG`.
- Tests refuse another plant's table, unknown sort columns, page size over 200, and non-owned plants.
</verification-summary>
</phase>

<phase number="2" name="Time series API" detail-plan="phase-2-plan.md">
<summary>`HistorianClient.ReadTagData` and `GET /api/browse/{name}/series` (tag tree)
and `GET /api/browse/{name}/trend` (up to 4 tags, start/end, thinned to 1,000 points).</summary>
<files-affected>~5: `HistorianClient.cs`, new `TrendThinning.cs`, `PlantBrowser.cs`, `WebApp.cs`, new tests</files-affected>
<depends-on>Phase 1</depends-on>
<verification-summary>
- Fake-Historian tests: boundary point and placeholder filtered, thinning keeps min/max, limits refused.
- One live read against a fresh `PlanTest` plant on a dev image.
</verification-summary>
</phase>

<phase number="3" name="Plants list, Summary and Tables in the page" detail-plan="phase-3-plan.md">
<summary>The list + detail layout, detail header with Stop/Start/Remove, Summary, and the
table browser, in the Timebase look.</summary>
<files-affected>~4: `index.html`, `app.js`, `styles.css`, `WebTests.cs`</files-affected>
<depends-on>Phase 1</depends-on>
<verification-summary>
- Tests pass; Chrome check on a dev image: list, Summary, sort, search, paging, deleted toggle, deep link, light and dark.
</verification-summary>
</phase>

<phase number="4" name="Time series in the page" detail-plan="phase-4-plan.md">
<summary>Master start/end selector, add/remove trend windows, tag tree with a 4-tag cap,
SVG charts with hover readout, windows remembered per plant.</summary>
<files-affected>~3: `app.js`, `styles.css`, `index.html`</files-affected>
<depends-on>Phases 2 and 3</depends-on>
<verification-summary>
- Chrome check on a dev image with a live `PlanTest` plant: two windows, master range change redraws both, values match the API.
</verification-summary>
</phase>

<phase number="5" name="Release 2.0.0-rc.3" detail-plan="phase-5-plan.md">
<summary>Documentation, KB article, acceptance gate P27, version bump, CI, tag, web
container switched to the CI image, live checks P23–P27 (and P10, P13 again), cleanup.</summary>
<files-affected>~12: docs, skill, Dockerfile, compose snippet, `package_release.py`, `verify.yml`, CLAUDE.md, README</files-affected>
<depends-on>Phases 1–4</depends-on>
<verification-summary>
- CI passes all jobs; tag `v2.0.0-rc.3`; live checks P23–P27 pass on the CI image.
</verification-summary>
</phase>

</phases>

<overall-success-criteria>
When all phases are complete:
- [ ] `dotnet build -c Release` and `dotnet test -c Release` pass with `FLOWSIM_TEST_PG` set.
- [ ] On the rc.3 container, a new `PlanTest` plant approved on the page appears in Plants;
      BluebirdBev does not appear.
- [ ] Its Summary matches `/api/browse/{name}`; a table page matches a direct `psql` read of
      the same table; a trend's points match a direct Historian read of the same tag and range.
- [ ] Removing the plant from its detail header removes it from the list (Dataset kept unless ticked).
</overall-success-criteria>

<execution-notes>
Work in the worktree `wt-timebase-look` on branch `web-timebase-look`. Each phase has its
own detailed plan. Execute in order (Phase 3 may start after Phase 1, Phase 4 needs 2 and 3):
1. `/implement-plan docs/11222026_plant-browser/phase-1-plan.md`
2. Verify, commit, then the next phase.

Standing rules for every phase: never print secrets (connection strings, client secrets);
live checks use simulation names starting with `PlanTest`; never touch BluebirdBev, its
container or the `Test` Dataset; do not change Pulse settings; never commit `docs/kb/`.
Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
</execution-notes>

<references>
- Historian read endpoint: `docs/timebase-api-findings.md:143`
- Page and container: `docs/plant-simulation.md` ("The web container")
- Previous web plan: `docs/11212026_interview-front-end/phase-7-plan.md`
- Release gates: `docs/release-2.0.0-specification.md`, `docs/release-2.0.0-acceptance.md`
</references>

</plan>
