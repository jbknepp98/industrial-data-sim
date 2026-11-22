<plan title="Phase 4: Golden batch drift and batch quality">

<overview>
Phase 4 of "Interview Front End for the Flow Data Simulator". Makes the three
golden-batch storylines available. In each, a process engineer compares
batches against the golden profile, sees a phase drifting, and links it to
yield loss and quality holds:

- **5a, chemicals reactor:** jacket fouling slows the heat phase.
- **5b, beverage syrup batching:** dissolve and blend times stretch and Brix
  drifts. Downstream, filler lots that consumed the drifted batches go on hold.
  Genealogy runs from the filler lot back to the syrup batch.
- **5c, pharma:** a granulation endpoint drifts. Batches outside the golden
  band need a deviation record and QA review before release.
</overview>

<context>
Assumes phase 3 is complete: batch lines, phases, records and tags exist.
</context>

<changes-required>

<change component="Model">
- **Golden profile:** `recipes[].golden`, optional. It defaults to the middle
  of each phase's range and setpoints. It also sets `yieldPercent` (target and
  sigma).
- **Drifts:** `batch.drifts[]`, each with:
  - `recipe` (optional), `phase` and `cause` (free text, for example "jacket
    fouling");
  - `from` and `until` (signed durations from the anchor);
  - `effect`: `phaseMinutesFactor` (for example 1.35 makes the heat phase 35%
    longer), `temperatureLagC` (the temperature trails the setpoint), and
    `yieldDelta` (for example −4 percentage points).

  A drift ramps in linearly over `rampDays`. A drift can be tied to a
  maintenance task: a PM ticket for "clean jacket" that ends the drift when
  it completes.
</change>

<change component="Engine and quality link">
- **Durations and yield:** a batch's phase duration and yield come from the
  golden values, plus the active drift effects, plus noise from its own stream.
- **Deviation:** every batch writes `deviation_score` (normalized distance from
  the golden profile across phase durations and temperature lag) and
  `golden=true/false` against a configurable threshold.
- **Lab results:** with a Quality area, each batch gets a sample whose result
  (for example assay %) comes from yield. An out-of-spec result creates a hold,
  through the same path as today.
- **Drift and maintenance:** a drift tied to a PM ends when that PM completes,
  which closes the loop the maintenance story needs.
</change>

<change component="Tables, tags, spec">
- **Columns:** `batches` gains yield_percent, deviation_score and golden.
- **New table:** `goldenprofiles`, with recipe_id, phase, golden_minutes and
  setpoints_json.
- **New tags:** `{Line}.BatchDeviation` (updated at the end of each phase) and
  `{Line}.HeatRate` (°C per minute while heating), so the drift is visible as
  a trend.
- **Atlas queries:**
  - `Line.BatchesOffGolden`;
  - `Recipe.GoldenComparison`, which returns phase-by-phase actual vs golden
    for the last N batches.
</change>

<change component="Library and preview">
- **5a fragment** (chemicals base): a jacket-fouling drift from 9 days before
  the anchor until a cleaning PM about 2 days before the anchor. Talk track:
  1. Explorer: overlay `Temperature` for a golden batch and a drifted batch.
  2. Connect: query `batches` ordered by deviation.
  3. Gage: "Why did yield drop on R1 last week?"
- **5b fragment** (beverage syrup base): a dissolve and blend drift, with Brix
  low by about 0.3, from 7 days before the anchor until 3 days before. The
  drifted batches feed filler lots that fail the Brix spec and go on hold.
  Talk track:
  1. Explorer: mix-tank `Phase` and `Temperature` next to the filler's
     `Quality.Brix`.
  2. Connect: `batchconsumption` → `lots` → `qualityholds`.
  3. Gage: "Which syrup batches caused last week's holds?"
- **5c fragment** (pharma base): an endpoint drift with `deviations` records.
  This adds a `deviations` table (deviation_id, batch_id, phase, description,
  opened_utc, closed_utc, qa_approver_id), and release waits for QA closure.
  Talk track: a batch comparison, then an audit-trail query of `deviations`
  with approver and times, then the Gage question "Which batches were
  released with deviations, and who approved them?"
- Preview: a batch comparison table (cycle time, yield, deviation), with the
  drift window shaded.
</change>

<change component="Tests">
- **Inside the drift window:** phase minutes and temperature lag match the
  factors; deviation scores rise; yield falls; holds appear when results go
  out of spec.
- **Outside the window, or after the cleaning PM:** batches return to within
  the golden band.
- **Snapshots:** resume equivalence includes a snapshot taken during the drift
  ramp.
- **Golden hashes:** packaging models' hashes are unchanged.
- **5b genealogy:** every held filler lot traces back through
  `batchconsumption` to a drifted syrup batch, and no undrifted batch leads to
  a Brix hold.
- **5c release:** a batch with an open deviation never releases before QA
  closure.
</change>

</changes-required>

<success-criteria>
<automated-verification>
- [ ] Build and full tests (Release, live PG) pass; packaging golden hashes unchanged
- [ ] Storylines 5a, 5b and 5c compose, validate and show their drifts in `PlantStorylineTests`
</automated-verification>
<end-to-end-verification>
- [ ] Docker runs of `PlanTestGolden…` for 5a, 5b and 5c (one at a time; remove each afterwards). For 5b, the held filler lots trace to drifted syrup batches. For 5c, `deviations` shows QA approvals before release. For 5a, `SELECT batch_id, deviation_score, yield_percent FROM …batches ORDER BY start_utc` shows the rise and the recovery after the PM; the `HeatRate` trend in Historian drops during the drift (verify script, 0 mismatches)
- [ ] Each talk-track step works as written against the running plant (Connect SQL runs; tags exist)
- [ ] Remove `PlanTestGolden…`
</end-to-end-verification>
</success-criteria>

<post-verification>
Commit: "plant: golden batch drift and batch quality". The engine track is complete.
</post-verification>

</plan>
