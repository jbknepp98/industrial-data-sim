<plan title="Phase 3: Batch area, part 1: recipes, phases, batch records, tags">

<overview>
Phase 3 of "Interview Front End for the Flow Data Simulator". Adds batch
processing for chemicals, dairy and pharma audiences (5 meetings asked about
golden batch, cycle time and batch comparison):

- a batch unit runs recipes as phase sequences;
- each phase has setpoint profiles and process tags;
- every batch writes batch and phase records.

Golden-batch comparison and drift come in phase 4.
</overview>

<context>
Assumes phase 2 is complete (`EngineVersion` 2). Lines today are continuous
packaging lines: a rate in cases per minute, and segments. A batch unit is a
new kind of line, so the 2-line limit still holds (for example 1 batch unit
plus 1 packaging line, or 2 batch units).
</context>

<changes-required>

<change component="Model">
**Files**: `PlantModel.cs`, `PlantModelLoader.cs`

- **Lines:** `lines[].kind` is `"packaging"` (the default; existing models are
  unchanged) or `"batch"`.
- **Batch lines** have `batch`:
  - `capacity` (kg or L), with `unit`;
  - `recipes[]`: `id`, `sku`, `phases[]`. Each phase has a `name` (Charge,
    Heat, React, Hold, Cool, Discharge, CIP …), `minutes` (a range),
    `setpoints` (temperatureC, pressureBar and agitatorRpm, each a start/end
    pair, ramped linearly), and a `levelPercent` start/end.
- **SKUs** made on a batch line are scheduled in batches: order quantity
  divided by capacity, rounded up.
- **Equipment** on a batch line uses the classes reactor, agitator, pump,
  jacket and valve, so failure modes work unchanged. For example an agitator
  seal failure stops the batch in its current phase.

Validation:
- each recipe's SKU exists and is made on that line;
- phase minutes are positive ranges;
- setpoints are within sane bounds (−20 to 300 °C, 0–20 bar, 0–500 rpm).

Error codes are named in the field-path style.
</change>

<change component="Engine">
**File**: `PlantEngine.cs`

- **Dispatch on a batch line:** start a batch (`BATCH-000001`) and run its
  phases in order. Phase durations are drawn from the range, using the
  per-batch stream `batch:{id}`.
- **Breakdowns:** a failure during a phase puts the line `Down`. On repair the
  phase resumes with its remaining time. Phase records note the interruption.
- **Order completion:** an order completes when its last batch discharges. The
  lot, quality and shipping flow is unchanged.
- **Feeding a packaging line (`feeds`):** a batch line can feed a packaging
  line (the beverage syrup base). A packaging order for a fed SKU starts only
  when a released batch is in stock and consumes it. The `batchconsumption`
  table records the batch id, the production order and the quantity, which
  gives genealogy from the filler lot back to the syrup batch.
- **Segments:** batch lines produce segments with the state `Running` (and a
  `Phase` name), Down, Maintenance, Idle or Changeover (CIP between different
  recipes).
- **Snapshots:** new state (current batch, phase, the remaining minutes) is
  captured in the snapshot. Bump `EngineVersion` to 3 if the layout changed
  after phase 2 shipped; otherwise fold it into version 2.
- **Golden hashes** for packaging-only models must not move.
</change>

<change component="Tables and tags">
**Files**: `PlantSchema.cs`, `PlantSignals.cs`, `PlantConnectSpec.cs`

Tables, only when a batch line exists:
- `recipes`: recipe_id, sku, phase_count.
- `recipephases`: recipe_id, sequence, phase, minutes_min, minutes_max, setpoints_json.
- `batches`: batch_id, production_order, recipe_id, line_id, start_utc,
  end_utc, size, status.
- `batchphases`: batch_id, sequence, phase, start_utc, end_utc,
  planned_minutes, actual_minutes, interrupted.

Tags per batch line:
- `BatchId`, `Phase` (strings);
- `Temperature`, `Pressure`, `AgitatorSpeed`, `Level` (numbers).

How the tag values behave:
- they follow the phase setpoint ramp plus stateless noise;
- while Down, temperature drifts toward ambient (an exponential approach) and
  agitator speed is 0;
- while Idle, the unit holds at ambient values.

The existing unit Status and Vibration tags are kept.

Atlas starter: batch units are Equipment; `Line.CurrentBatch` and
`Equipment.BatchHistory` are query properties.
</change>

<change component="Base model and preview">
Three bases, each with 2 lines and minimal staff:

- `examples/storylines/bases/chemicals-batch.json`: one reactor line with 2
  recipes, plus one drumming/packaging line.
- `examples/storylines/bases/beverage-syrup-batch.json`: a syrup mix tank
  (batch line: dissolve, blend, Brix and pH hold, transfer) feeding one filling
  line. A filling order consumes completed syrup batches. If no released batch
  is available, the filler waits, Idle with the reason `Waiting for syrup`.
  This closes the "mixing not modeled" gap.
- `examples/storylines/bases/pharma-batch.json`: a granulation or blending
  vessel line, plus one packaging line, with quality enabled.
- Preview: the Gantt chart shows batch phases as sub-segments, and the summary
  adds batches and average cycle time per recipe.
</change>

<change component="Tests">
`PlantBatchTests.cs` (new):

- **Phases tile each batch:** no gaps or overlaps, apart from Down intervals,
  which are recorded.
- **Tags follow the ramp:** Temperature at the middle of a phase lies on the
  setpoint ramp within the noise band.
- **Failure mid-phase:** the line goes Down, the phase resumes with its
  remaining time, and the ticket loop is unchanged.
- **Order completion:** the order completes after its last batch, and a lot is
  created.

`PlantSnapshotTests`:
- add the batch base to `Instants()`, including a snapshot instant in the
  middle of a phase and one in the middle of a failure;
- packaging golden hashes stay unchanged.

Live Postgres test for the new tables.
</change>

</changes-required>

<success-criteria>
<automated-verification>
- [x] Build and full test suite (Release, `FLOWSIM_TEST_PG` set) pass
- [x] Packaging golden hashes unchanged
</automated-verification>
<end-to-end-verification>
- [x] `plant preview` of `chemicals-batch.json` shows batches with phases; render the page and check the Gantt chart visually
- [x] Build and run `PlanTestBatch…` in Docker: Historian `…R1.Temperature` follows the Heat ramp for a known batch (run the verify script, 0 mismatches); `batchphases` rows match the preview
- [x] Remove `PlanTestBatch…`

Results (November 22, 2026, branch `feature/phase-3-batch-area`, image
`flow-data-simulator:2.0.0-dev-batch` built from this source; `2.0.0-dev` and
BluebirdBev untouched):

- Release build with 0 warnings. The full suite passed 1053 of 1053 tests with
  `FLOWSIM_TEST_PG` set, including the new `PlantBatchTests` and
  `PlantStoreTests.BatchTablesAreCreatedFilledAndRemoved`. The four packaging
  golden hashes are unchanged; EngineVersion is 3.
- Resume equivalence covers all three batch bases at fixed instants, plus
  snapshots taken mid-phase, mid-failure (chemicals) and while the filler
  waits for syrup.
- The rendered preview of the chemicals base shows phases as shades of green
  with a tick per batch. A batch breakdown charts Temperature: it ramps in
  Heat, cools while Down, and resumes the phase after the repair.
- `PlanTestBatch220600` (chemicals base, 2-day backfill) went live in under a
  minute. Historian matched the engine on 33,403 points (0 mismatches, 0
  missing), and all 28 `batchphases` rows equaled the preview.
- `R1.Temperature` lay on the Heat ramp for all 6 completed Heat phases (max
  residual 0.42 °C, mean about 0). Example: BATCH-000003 (RCP-ALKYD-A, 40 → 180
  °C) read 109.96 °C at mid-phase.
- After `docker stop` (exit 130) and start, it logged `plant.snapshot_resumed`
  and matched again (33,414 points, 0 mismatches). It was then removed (24
  tables dropped, none left).

Deviations from the text above, all small:

- Natural keys were added as first columns: `recipephases.recipe_phase_id`,
  `batchphases.batch_phase_id` and `batchconsumption.consumption_id`.
- `feeds` sits inside the batch line's `batch` section, as
  `{ line, consumption: [{ sku, intermediate, perCase }] }`.
- Fed intermediates come from make-to-order batch orders with no sales order.
- The wait reason names the intermediate: `Waiting for Berry syrup`.
- `Line.BatchPhases` and `Line.BatchGenealogy` were added to the required
  query properties.
</end-to-end-verification>
</success-criteria>

<post-verification>
Commit: "plant: batch area (recipes, phases, batch records, process tags)". Proceed to phase 4.
</post-verification>

</plan>
