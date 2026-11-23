<plan title="Phase 4: Time series in the page">

<overview>
Phase 4 of the Plant browser plan (`plan.md`). Fills the Time series section: one master
start/end selector for the plant, any number of trend windows that all follow it, each with
up to 4 tags picked from a line → machine tree, drawn as SVG charts in the preview's style.
</overview>

<context>
Assumes Phase 2 (`/api/browse/{name}/series`, `/api/browse/{name}/trend`) and Phase 3 (list +
detail, section tabs, hash state) are complete, and the dev image with `PlanTestBrowse` from
Phase 3 is still running. Chart style reference: `tools/plant-preview/plant-preview.js:150-220`
(axes, `--rule` grid, `--accent` lines, `vector-effect: non-scaling-stroke`).
</context>

<changes-required>

<change component="Master time selector">
**File**: `src/IndustrialDataSim.Web/wwwroot/app.js`, `index.html`
**Changes**: A bar at the top of `#pane-series` (styled like Explorer's "Last 1d · LIVE" control:
mono 11 px, outline button):
- Two `<input type="datetime-local">` fields (shown and entered in UTC, labelled "UTC") plus
  quick picks: Last 1 h, 8 h, 24 h, 7 d, Whole run.
- Default: the last 24 h ending at `range.maxUtc`, clipped to `range.minUtc`.
- Validation in the page mirrors the server (start < end, span ≤ 31 days, inside the run);
  an invalid range shows the reason under the bar and does not query.
- Apply (and every quick pick) redraws all trend windows with one `/trend` call per window.
- Refresh re-reads with the same range.
</change>

<change component="Trend windows">
**File**: `src/IndustrialDataSim.Web/wwwroot/app.js`
**Changes**:
- "Add trend" appends a window (a `.card` panel with a 42 px header: title "Trend N",
  tag chips with remove ×, "Tags…" button, remove-window ×). A new plant starts with one
  window holding the first line's first two tags.
- "Tags…" opens a picker: the `/series` tree (line → machine → tags with measure), a filter
  box, checkboxes; the 5th selection is disabled with "Up to 4 tags per trend". Closing applies.
- Drawing: one SVG per window, full width, 220 px tall; shared time axis with 4–6 UTC ticks;
  one y-axis per distinct measure, up to 2 shown (left, right); further measures are scaled
  to the first axis and say so in the legend. Colors from `--tb-state-1…4`
  (`#31aee4 #36e063 #f59e0b #ef4444`), legend chips match. Booleans and text draw as step
  lines / state bands. A tag with no points shows "No values in this range" in its legend chip.
  "Thinned from N points" appears in the legend when `thinned`.
- Hover: a vertical cursor line and a readout of each tag's value at the nearest point.
- Errors from `/trend` show inside that window with the code; other windows still draw.
- State per plant (windows and their tags; not the range) saved in `localStorage`
  key `flowsim.trends.<simulation>` inside try/catch; restored on select; tags that no longer
  exist are dropped.
- Theme switch redraws (the charts read colors when they draw), as the preview already does.
</change>

<change component="Styles">
**File**: `src/IndustrialDataSim.Web/wwwroot/styles.css`
**Changes**: `.timebar`, `.trend` panel, `.tag-chip` (mono 11 px, colored dot), `.picker`
popover (`--tb-popover`, `--tb-shadow-lg`, radius 10 px, max-height 60vh scroll), chart axis
text `--muted` 10 px mono.
</change>

</changes-required>

<success-criteria>

<automated-verification>
- [x] Builds: `dotnet build -c Release`; full suite passes with `FLOWSIM_TEST_PG` set
- [x] `node --check src/IndustrialDataSim.Web/wwwroot/app.js` passes
</automated-verification>

<end-to-end-verification>
Rebuild the dev image and recreate the web container on it (as in Phase 3). In Chrome with `PlanTestBrowse`:
- [x] Time series opens with one window, two tags, last 24 h; points visible.
- [x] Add a second window with a vibration tag and a state tag; both draw.
- [x] Change the master range to "Last 8 h": both windows redraw to the same range.
- [x] One window's first-tag values equal `GET /api/browse/PlanTestBrowse/trend?…` for the same range
      (spot-check 3 points by hover readout).
- [x] A 7-day range on a 10-second tag reports "Thinned from N points" and draws ≤ 1,000 points.
- [x] A fifth tag cannot be selected; a 32-day range is refused in the page with the reason.
- [x] Reload: both windows and their tags come back; switching to another plant and back keeps them.
- [x] Light and dark both readable; 375 px wide: windows stack, no page-wide horizontal scroll.
Then remove `PlanTestBrowse` from its detail header with "Also delete its Dataset" ticked, and
confirm in Historian that the Dataset is gone (`GET /api/datasets/PlanTestBrowse/exists` → false).
Put the web container back on `flow-data-simulator-web:2.0.0-rc.2` until Phase 5.
</end-to-end-verification>

</success-criteria>

<verification-record>
Done November 23, 2026 on the test container `flow-data-simulator-web-look` (127.0.0.1:8481,
image `flow-data-simulator-web:2.0.0-rc.3-dev-look` built from the branch), not on the
user's 8480 container, which stayed on rc.2 throughout; BluebirdBev untouched.
- Trends read on Gallo (read-only) and on `PlanTestBrowse`, approved from a short interview
  through Gage and removed afterwards with its Dataset (`exists` → false; its 23 tables gone).
- Default window: first line's first two number tags (Speed, GoodCount) rather than its first
  two tags, which are text (State, SKU).
- Hover spot-check: 2 of 3 readouts equal to the digit; the third differed by one count
  because of the hover position, and the drawn series equalled a fresh API read point for point.
- 7 days of Speed: 31,836 raw points drawn as 698. 375 px checked in the app's browser
  (the page refuses iframes: `frame-ancestors 'none'`); no page-wide horizontal scroll.
- Four PlanTest plants left by test runs before the cleanup-deadlock fix were removed with
  `plant remove … --confirm`.
</verification-record>

<post-verification>
Commit: "Time series: master time range and trend windows of up to 4 tags each".
</post-verification>

</plan>
