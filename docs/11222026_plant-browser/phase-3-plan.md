<plan title="Phase 3: Plants list, Summary and Tables in the page">

<overview>
Phase 3 of the Plant browser plan (`plan.md`). Replaces the Plants table with a list +
detail layout in the Timebase look: plant list on the left; for the selected plant a header
with status and Stop / Start / Remove, and section tabs Summary · Time series · Tables.
This phase fills Summary and Tables; Time series shows "Coming in this build" placeholder
text until Phase 4 (never shipped in that state: Phase 4 lands before the release).
</overview>

<context>
Assumes Phase 1 is complete (`/api/browse/{name}`, `/tables`, `/tables/{table}/rows`;
`/api/plants` lists only plants this page created). The page is plain HTML/JS with no
framework (`src/IndustrialDataSim.Web/wwwroot/`), styled with the Timebase tokens in
`styles.css`. The interview id lives in the URL hash as `#interview=<id>`.

Owner direction (November 22, 2026): the page is **Timebase Plant Simulator** and follows
Historian's layout. Plants is already a Historian Datasets-style list card (filter box,
**New plant**, rows with chevron, status dot, name and uppercase metric labels over mono
values; `expandedPlants` keeps opened rows across the 5-second poll). **New plant** in the top strip
is the only way to start a plant (the Plants toolbar has only the filter), and startup opens
Plants when the page has built a plant: keep both. The hash may already carry `&step=review`
for the interview; parse and write it together with `plant`, `section` and `table`. Build the list
column from those rows and keep the page header (28 px title, one-line description) and
the top-strip tabs; do not reintroduce a card header bar for the list.

Known pitfalls from rc.2: the Plants view redraws every 5 seconds, which used to reset an open
Remove dialog (fixed with `pendingRemoveDataset`) and invalidate element references. This phase
must not redraw the table grid on the poll.
</context>

<implementation-note>
Built November 23, 2026 as **list, then the plant's page** instead of a side-by-side
list and detail: the owner-approved Historian Datasets-style list stays the overview,
and opening a plant shows its page with a "Plants / name" breadcrumb (as the Timebase
apps move from a list into one item). Markup ids follow that: `#plants-overview`,
`#plant-detail`, `.detail-head`, `.section-tabs`, `#pane-summary|series|tables`,
`#table-list`, `#grid`. After Remove the page returns to the list. Everything else
below (hash state, polling rules, Summary cards, table browser, limits) is as planned.
</implementation-note>

<changes-required>

<change component="Markup">
**File**: `src/IndustrialDataSim.Web/wwwroot/index.html`
**Changes**: Replace the `#view-plants` card with:

```html
<main id="view-plants" class="browse" role="tabpanel" aria-labelledby="tab-plants" hidden>
  <nav class="plant-list card" aria-label="Simulations">
    <div class="eyebrow">Simulations</div>
    <ul id="plant-list"></ul>                       <!-- li > button[data-plant] -->
    <p class="muted" id="plant-list-empty" hidden>No plants yet. Approve one at the end of an interview.</p>
  </nav>
  <section class="plant-detail" id="plant-detail" aria-live="polite">
    <header class="detail-head card">               <!-- name, status badge, dataset, actions, remove confirm -->
      …
    </header>
    <div class="section-tabs" role="tablist">        <!-- Summary | Time series | Tables -->
      <button role="tab" id="sec-summary" aria-selected="true">Summary</button>
      <button role="tab" id="sec-series">Time series</button>
      <button role="tab" id="sec-tables">Tables</button>
    </div>
    <div id="pane-summary"></div>
    <div id="pane-series" hidden></div>
    <div id="pane-tables" hidden></div>
  </section>
  <p class="error" id="plants-error" hidden></p>
</main>
```
</change>

<change component="Browser script">
**File**: `src/IndustrialDataSim.Web/wwwroot/app.js`
**Changes**: Replace `loadPlants` / the plants table renderer with a `browse` section:

- **Hash state**: parse and write `interview`, `plant`, `section`, `table` together
  (`#interview=…&plant=PlanTestX&section=tables&table=plantestx_lots`). Opening Plants with
  no `plant` selects the first plant. Unknown plant in the hash → the empty detail with
  "That plant is not in this page's list."
- **List** (`GET /api/plants`, every 5 s while Plants is open): each item shows the name,
  status badge (existing badge classes), Dataset, and "delivered through …". Polling updates
  list items and the detail header in place; it never rebuilds the Summary or Tables panes.
- **Detail header**: name, status badge, hosting message, Dataset; Stop / Start / Remove with the
  existing calls and the existing remove confirm (checkbox "Also delete its Dataset …",
  `pendingRemoveDataset` kept). After Remove, select the next plant.
- **Summary** (`GET /api/browse/{name}`, on select and on a Refresh button): four cards —
  *Run* (start, anchor, delivered through, snapshot, applied sequence), *Where the data is*
  (Dataset with "created by this page, 365-day purge" when `datasetCreated`, table prefix,
  counts), *Lines and equipment* (line → equipment list with class), *Storylines and planted
  incidents* (incidents as a small table: when, line, machine, failure mode, note). Downloads
  row with the existing links (demo guide, Connect spec, Atlas starter) and the three
  Gage kit files.
- **Tables** (`GET /api/browse/{name}/tables` on first open; rows on demand):
  - left: table list with purpose, row count and deleted count;
  - toolbar: search box (debounced 300 ms; Enter searches at once), "Show deleted rows" toggle,
    Refresh, and "N rows · page P of M";
  - grid: sticky header; click a header to sort (toggle desc/asc, arrow shown); default
    `row_version` desc; numbers and timestamps in mono, right-aligned numbers; `null` muted;
    a cut cell shows its full text in `title`; `is_deleted` rows dimmed;
  - pager: Previous / Next, disabled at the ends; page > 501 not offered;
  - any API error shows in the pane with its code, keeping the last good page visible.
- Remove the old Plants table code and its "read-only" branch for plants created elsewhere
  (they are no longer listed).
</change>

<change component="Styles">
**File**: `src/IndustrialDataSim.Web/wwwroot/styles.css`
**Changes**: `.browse` grid `260px 1fr` (single column under 980 px, list becomes a select-like
strip); list items like the sidebar nav items (selected = accent background, blue left bar);
`.section-tabs` like Explorer's top tabs (14 px/500, 2 px blue underline on the selected tab);
`.grid` table with sticky header on `--panel`, 10 px uppercase header labels, row hover
`--tb-input-surface`, horizontal scroll inside the pane only; pager and toolbar controls use the
existing button styles. Light and dark via the existing tokens only.
</change>

<change component="Tests">
**File**: `tests/IndustrialDataSim.Tests/WebTests.cs`
**Changes**: In `ThePageAndTheSharedRendererAreServed`, assert `app.js` contains `/api/browse/`
and the page contains `id="plant-list"`, `id="pane-tables"`. Existing Plants-flow tests that
drive the API keep passing.
</change>

</changes-required>

<success-criteria>

<automated-verification>
- [x] Builds: `dotnet build -c Release`
- [x] Full suite passes with `FLOWSIM_TEST_PG` set: `dotnet test -c Release`
- [x] `node --check src/IndustrialDataSim.Web/wwwroot/app.js` passes
</automated-verification>

<end-to-end-verification>
Live run: confirm with the user first (it costs one interview, about 40k input tokens through Gage).
1. Build a dev image from the worktree: `docker build -f deployment/web/Dockerfile -t flow-data-simulator-web:2.0.0-dev-browser .`
2. Recreate `flow-data-simulator-web` on the dev image with the same compose snippet and env
   (`FLOWSIM_WEB_ENV=~/flow-data-simulator/config/web.env`, `FLOWSIM_CERTS`, `TIMEBASE_NETWORK`);
   `bluebirdbev-plant` stays untouched.
3. In Chrome: run a short interview and approve a plant named `PlanTestBrowse`.

Checks (Chrome, http://localhost:8480):
- [x] Plants lists only page-built plants (Gallo on 8481); BluebirdBev is absent.
- [x] Summary values equal `curl -s localhost:8480/api/browse/PlanTestBrowse`.
- [x] Tables: every table's count equals `SELECT count(*)` via `docker exec flow-data-sim-pg psql …` (read-only).
- [x] Sorting `created_utc` asc then desc reorders; search for a ticket id finds it; `%` finds nothing extra.
- [ ] Page 2 has no overlap with page 1; Previous is disabled on page 1.
- [x] "Show deleted rows" changes the total only if the table has soft-deleted rows.
- [ ] Deep link `#plant=PlanTestBrowse&section=tables&table=<prefix>maintenancetickets` opens that table after a reload.
- [ ] Open Remove dialog, tick the box, wait 10 s: the box stays ticked; Cancel.
- [ ] Light and dark both readable; at 375 px wide the list stacks above the detail with no page-wide horizontal scroll.
Keep `PlanTestBrowse` running for Phase 4.
</end-to-end-verification>

</success-criteria>

<post-verification>
Commit: "Plants view: list of this page's plants with Summary and a read-only table browser".
</post-verification>

</plan>
