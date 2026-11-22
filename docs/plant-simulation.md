# Plant simulation

Design and owner decisions: [design-plant-simulation.md](design-plant-simulation.md).
Example: [examples/plants/bluebird-bev.json](../examples/plants/bluebird-bev.json),
a rebuild of the Bluebird Bev demo.

It simulates a small plant on one clock:

- **Production → Maintenance loop:** always on.
- **Quality and Shipping areas:** optional.
- **Certification compliance:** optional (`maintenance.certification`).

The milestones so far:

1. Offline engine and preview.
2. Records written to Postgres (`flow_data_sim`).
3. Live run in Docker: rows to Postgres and tags to Historian.
4. The interview skill.
5. The Quality and Shipping areas.
6. The Connect mapping spec and the Atlas starter.
7. Certification compliance for technicians and operators (`EngineVersion` 2).

## Commands

```
plant validate <model> [anchorUtc]
plant preview <model> <output.json> [hoursAfterAnchor 1-720, default 24] [anchorUtc]
```

- **The anchor is "now"** unless you give one. `timeline.backfill` and incident
  times (`"at": "-30h"`) are resolved against it. The same model, seed and anchor
  always produce the same history, byte for byte.
- **The preview file holds:**
  - a summary;
  - each table's final rows under its physical name (`bluebirdbev_maintenancetickets` …);
  - the line state segments and the breakdowns;
  - every tag sampled at 60 s.
- **The command prints row counts only.**

## Postgres: flow_data_sim (milestone 2)

The database runs in the container `flow-data-sim-pg` on the Timebase Docker
network (alias `flow-data-sim-pg`, port 5432). From the Mac it is published only
on `127.0.0.1:5435`. Run
[deployment/postgres/01_bootstrap.sql](../deployment/postgres/01_bootstrap.sql)
once as the database owner. It creates:

- **Roles:** `flowsim_writer` (the simulator) and `flowsim_reader` (for Connect).
  The reader can read every simulation table and nothing else.
- **`flowsim_row_version`:** a shared sequence, plus the trigger that stamps
  `row_version` on every insert and update. This is the connector's watermark.
- **`flowsim_simulations`:** the registry. It records which simulation owns
  which table prefix, the admitted model and anchor, and delivery progress.

Commands (each needs `FLOWSIM_PG_CONNECTION`, a writer connection string kept in
a private env file):

```
plant build <model> [anchorUtc]       admit the plant and create its 16 tables
plant sync <simulation> [throughUtc]  apply records up to a time (never past now)
plant status [simulation]             registration and progress
plant remove <simulation> --confirm   drop that simulation's tables and registration only
```

- **Ownership:** a simulation only creates or drops tables with its own prefix.
  Admission is refused when another simulation owns the prefix, when
  unregistered tables already use it, or when a different model is already
  registered under the name. Repeating a build with the same model and anchor
  changes nothing.
- **No duplicates or gaps:** each engine change has a sequence number. Rows and
  the registry's `applied_sequence` commit in one transaction, so a crash at
  any point leaves a state that the next sync continues exactly.
- **The stored model and anchor are the source of truth.** A restart replays
  exactly the same history, and the engine is tested so that replaying further
  never changes earlier records. A model that no longer reproduces the stored
  history stops with `plant.history_mismatch`.
- **Every table** has its natural key as the primary key, plus `row_version`,
  `modified_utc` (the simulated change time), `is_deleted` and `deleted_utc`.

Integration tests run against a real database when `FLOWSIM_TEST_PG` holds a
writer connection string. They create and remove their own uniquely named
simulations.

## Live run in Docker (milestone 3)

```
plant run <simulation>
```

The admitted plant is delivered until the container stops (`docker stop` sends
SIGINT; exit code 130). Each cycle runs the engine from the latest snapshot
through the delivery point, plus a look-ahead of the longest `warningHours` + 1
hour. The look-ahead means vibration already rises before a failure the engine
will produce. Only rows and tag samples at or before now are delivered.

- **Rows:** Postgres, exactly once (see above).
- **Tags:** Historian, in batches of at most 1000 points, written to the Dataset
  in `TIMEBASE_DATASET`. The publish intent (`sending_until_utc`) is recorded
  before each batch and cleared after it. A graceful stop always finishes the
  batch in flight. If the process is killed or loses power mid-batch, the next
  start sends that batch again (event `plant.interrupted_batch_resent`, counted
  as `resentBatches` in status). Plant tags are deterministic, so a resent point
  has the same tag, time and value. This differs from the 1.x Uncertain rule on
  purpose (owner decision, November 22, 2026).
- **Snapshots:** once rows and tags are delivered past a whole hour of plant
  time, the engine state at that hour is saved in `flowsim_snapshots` (one row
  per simulation, created automatically, removed with the simulation). Later
  cycles and restarts resume from it, so a cycle's cost does not grow with the
  plant's age. A resumed run is identical to a run from the plant start; the
  tests compare every row, line state and tag value at many snapshot instants.
  A snapshot from another engine version, for another model, damaged, or
  ahead of what was delivered is ignored (`plant.snapshot_ignored`) and the
  plant replays from its start: slower, never different.
- **Sleep, power loss and long stops:** the plant computes everything from the
  calendar, not from elapsed run time. After a laptop wakes or the container
  restarts, the next cycles deliver the missing period exactly as if it had run
  throughout. A catch-up advances at most 6 hours of plant time per cycle, each
  from the newest snapshot, so even a week's gap is delivered in bounded steps.
- **Retention check:** at start the host reads the Dataset's purge age. If the
  backfill begins before it, the host logs `plant.retention_warning`: Historian
  will drop those oldest points, while Postgres keeps every row.
- **One host per simulation:** a Postgres advisory lock refuses a second host
  (`plant.host_running`).
- **Log:** the host prints one JSON line per change of condition:
  `plant.started`, `plant.snapshot_resumed` or `plant.snapshot_ignored`,
  `plant.backfilling` (every 10 minutes), `plant.live` and `plant.stopped`.

Measured engine cost for the Bluebird plant, one cycle with a 13-hour look-ahead:

| Plant age | From the plant start | From a snapshot | Snapshot size |
| --- | --- | --- | --- |
| 6 days | 1 ms | 1 ms | 3 KB |
| 90 days | 26 ms | 0.1 ms | 2 KB |
| 1 year | 66 ms (+8 ms signal setup) | 0.5 ms | 3 KB |

Engine changes that alter its output must bump `PlantEngine.EngineVersion`:
running plants replay their stored history and would otherwise stop with
`plant.history_mismatch`. `PlantSnapshotTests` pins the output with hashes.

**`EngineVersion` 2** (certification compliance) changed only the snapshot
layout: it adds certificate state. Output for plants without
`maintenance.certification` is byte-identical (the golden hashes did not
move). A plant restarted on the new image logs `plant.snapshot_ignored` once
(its version 1 snapshot), replays from its start to the same history, and
saves version 2 snapshots from then on.

The env file passed with `--env-file` needs the `TIMEBASE_*` settings (Historian,
Pulse, client credentials, CA bundle and Dataset) and `FLOWSIM_PG_CONNECTION`
(host `flow-data-sim-pg`).

**Live acceptance (November 21–22, 2026).** A 6-hour smoke plant reached
`plant.live` in about 5 seconds. A planted labeler jam then failed in real time
at 00:36:33 UTC. The detector wrote the downtime event and an assigned High
maintenance ticket at 00:41:33, exactly `detectionMinutes` later, while it
happened. Historian matched the engine on all 6,636 changed values (0
mismatches, 0 missing). `docker stop` exited 130 with 0 uncertain batches, and
`plant remove` dropped the smoke plant's 16 tables. BluebirdBev (6-day backfill)
went live in about a minute and matched on 139,305 checked points.

## Quality and Shipping areas (milestone 5)

Both are optional model sections; see the Bluebird example. When either is
present, each completed order becomes a **lot** (`lots`).

- **`quality`:**
  - each lot is sampled at completion (`qualitysamples`);
  - after `labMinutes`, one result per parameter is written (`qualityresults`).
    The value is the same function the line's in-line analyzer tag
    (`<Line>.Quality.<Parameter>`) reads, so a lab result equals the analyzer
    reading when the lot was made;
  - each parameter has a target, limits, a noise sigma, per-SKU offsets
    (`skuOffsets`) and planted `excursions` (a shift over a time window);
  - any out-of-spec result opens a hold (`qualityholds`) for `holdMinutes`,
    after which the lot is released.
- **`shipping`:**
  - a released lot is received into stock (`inventorytransactions`);
  - it ships after `delayHours` with one of the `carriers` (`shipments`);
  - the sales order becomes Shipped, and `on_time` compares the ship time with
    the due date, so holds can make orders late;
  - the tag `<Root>.Warehouse.CasesOnHand` follows the inventory transactions.
- **Without shipping,** a sales order is Completed when produced.

Specification limits per parameter and SKU are in `qualityspecs`.

## Certification compliance

An optional `maintenance.certification` section (storylines
`uncertified-technician-closure` and `uncertified-operator-run`). Without it,
nothing below happens and no extra tables exist.

```json
"certification": {
  "validDays": 365,
  "issued": [{ "technician": "E1008", "skill": "mechanical", "at": "-375d" }],
  "renewals": [{ "technician": "E1008", "skill": "mechanical", "at": "-1d" }],
  "safetyCritical": ["FL201-VALVE"],
  "whenNoneCertified": "assignAnyway",
  "operators": {
    "workInstructions": [{ "id": "WI-L2-BERRY", "line": "L2", "skus": ["BB-BR-20"], "validDays": 180,
                           "issued": [{ "operator": "E1010", "at": "-186d" }] }],
    "whenOperatorUncertified": "runAnyway",
    "rejectPercentWhenUncertified": 4.5
  }
}
```

- **Certificates:** each `issued` or `renewals` entry gives a technician a
  certificate for one of their skills, from `at` (a signed whole-minute
  duration from the anchor) for `validDays` (default 365). A technician and
  skill with no entry holds no certificate. `trainingrecords` gets one row per
  certificate: written at the plant start for certificates issued earlier
  (with their true issue time), or at the issue time. Its `status` becomes
  `Expired` at `expires_utc`, or `Renewed` when a renewal replaces it first.
- **Safety-critical work:** a ticket whose failure or PM code is in
  `safetyCritical` goes to a certified technician who is free and on shift.
  When there is none:
  - `wait` (the default): the ticket waits for one, so the downtime runs
    longer;
  - `assignAnyway`: a skilled technician without a current certificate takes
    it, and the status history says so.

  Every safety-critical assignment writes a `workcompliance` row with
  `certified` and the reason, for example `certificate expired 2026-11-11`.
  Other work is assigned exactly as before.
- **Operators (`operators`):** each work instruction covers some SKUs on one
  line, and operators hold it for its `validDays` from each `issued` entry (a
  later entry renews it). When a run of a covered SKU starts, the shift's
  operator is checked and an `operatorcompliance` row is written. If the
  certificate is not current:
  - `runAnyway`: the run starts, at `rejectPercentWhenUncertified` when set,
    so the run's reject cases and the line's `RejectCount` slope rise;
  - `supervisorSignoff`: the row gets `signoff_utc`, the line waits in state
    `Signoff` for `signoffMinutes`, then the run starts at the normal reject
    rate.
- **Planted incidents** may name a `shift`: the loader moves the incident into
  that shift (it is kept when it falls at least two hours before the shift
  ends, otherwise moved to two hours after the shift's next start). Storyline
  3a uses it so the planted valve leak always meets the night mechanic.
- **Tables:** `trainingrecords` and `workcompliance` with the section;
  `workinstructions` (SKUs comma-separated) and `operatorcompliance` with
  `operators`. No new tags: the story joins the existing unit Status,
  Vibration and RejectCount tags at the record times.
- **Connect spec and Atlas starter:** relationships for the new tables, and
  the query properties `Person.ExpiredCertifications`,
  `Equipment.UncertifiedWork` and (with operators) `Line.UncertifiedRuns`.
- **Preview:** the summary lists `uncertifiedAssignments` and
  `uncertifiedRuns`, and the review page shows them.
- **Validation errors:** `plant.unknown_certification_target` (unknown
  technician, operator, skill, line, SKU or code),
  `plant.certification_time` (not a signed whole-minute duration) and
  `plant.invalid_certification_policy` (unknown policy, or `assignAnyway`
  without safety-critical codes).

## Batch units

A line with `"kind": "batch"` is a batch unit (a reactor, mix tank or
granulator). It counts as one of the two lines. Lines without `kind` are
packaging lines, exactly as before. Bases:
`examples/storylines/bases/chemicals-batch.json` (reactor feeding a drumming
line), `beverage-syrup-batch.json` (syrup mix tank feeding a filling line) and
`pharma-batch.json` (granulator selling bulk granulate in multi-batch orders,
plus a packaging line, with Quality).

```json
{ "id": "R1", "name": "Reactor R-101", "kind": "batch", "changeoverMinutes": 90, "skus": ["RES-A", "RES-B"],
  "equipment": [{ "id": "AG-101", "name": "Reactor 101 Agitator", "class": "agitator" }],
  "batch": {
    "capacity": 8000, "unit": "kg",
    "recipes": [{ "id": "RCP-ALKYD-A", "sku": "RES-A", "phases": [
      { "name": "Heat", "minutes": { "minimum": 60, "maximum": 90 },
        "setpoints": { "temperatureC": { "start": 40, "end": 180 }, "pressureBar": { "start": 1.0, "end": 2.5 },
                       "agitatorRpm": { "start": 80, "end": 120 } },
        "levelPercent": { "start": 75, "end": 75 } } ] }],
    "feeds": { "line": "D1", "consumption": [{ "sku": "DRUM-A", "intermediate": "RES-A", "perCase": 200 }] } } }
```

- **Recipes:** one per SKU on the unit, 1–12 phases. Each phase draws its
  duration from `minutes`, using the batch's own random stream
  (`batch:BATCH-000001`). Setpoints ramp linearly over the phase's running
  time. Bounds: −20 to 300 °C, 0–20 bar, 0–500 rpm, level 0–100 %.
- **Orders** for a batch SKU are in the unit's `unit` and run as
  ceil(quantity / capacity) batches, back to back. The order completes when its
  last batch discharges, and the lot, quality and shipping flow is unchanged.
- **Breakdowns:** failure modes on the unit's equipment work as on any line. A
  failure mid-phase puts the line Down. After the repair, the phase resumes for
  its remaining time. Its `batchphases` row is marked `interrupted`, and its
  `actual_minutes` include the Down time. Batch units have no minor stops.
- **CIP:** changing recipes uses `changeoverMinutes`, in state `Changeover`
  with the reason `CIP <from> to <to>`.
- **`feeds`:** the unit's SKUs named as `intermediate` are consumed by the fed
  packaging line, never ordered by customers.
  - A filling order for a consuming SKU creates a batch order (no sales order)
    when released stock plus batches already on order cannot cover it.
  - The filling order starts only when released batches hold
    quantity × `perCase`. It consumes them oldest first, and each take is a
    `batchconsumption` row (batch, production order, quantity).
  - With no stock, the fed line waits, Idle, with the reason
    `Waiting for <intermediate description>` (for example `Waiting for Berry syrup`).
  - Batch `status`: `Running`, then `Released` at discharge (or `Quarantine`
    until its release decision when the plant has `quality.batchRelease`, see
    below), and `Consumed` when used up. Only released batches are consumed.
- **Tags per batch line:** `BatchId`, `Phase` (`CIP` during a changeover),
  `Temperature`, `Pressure`, `AgitatorSpeed` and `Level`, plus `State`, `SKU`,
  `OrderNumber` and each unit's `Status` and `Vibration`. A batch line has no
  `Speed` or counts.
  - While Running, the values follow the phase ramp plus stateless noise.
  - While Down, temperature and pressure decay toward ambient (20 °C, 1.0 bar;
    time constant 45 minutes), the agitator is 0 and the level holds.
  - After the repair (EngineVersion 4), the temperature climbs back from where
    it cooled at up to 3 °C per minute until it meets the phase ramp. A unit
    that starts a phase warmer than the ramp (the next batch right after a hot
    discharge) settles onto it with a 20-minute time constant.
  - Between batches, the values are ambient, empty and still.
- **Tables:** `recipes`, `recipephases` (setpoints as JSON), `batches` and
  `batchphases` with any batch unit; `batchconsumption` with `feeds`.
- **Connect spec and Atlas starter:** relationships for the new tables, and
  the query properties `Line.CurrentBatch`, `Line.BatchPhases`,
  `Equipment.BatchHistory` and (with `feeds`) `Line.BatchGenealogy`.
- **Preview:** segments carry `batch` and `phase`, the summary adds `batches`
  (count, discharged, interrupted phases, mean cycle minutes per recipe), and
  the review page draws phases as shades of green with a tick at each batch.
- **Validation errors:** `plant.invalid_line_kind`, `plant.batch_required`,
  `plant.batch_on_packaging_line`, `plant.recipe_sku`, `plant.ramp_required`,
  `plant.setpoints_required`, `plant.batch_sku_shared` (a SKU on both a batch
  and a packaging line) and `plant.invalid_feeds`.

**`EngineVersion` 3** adds batch state to snapshots: the batch in progress,
its phase and remaining seconds, released intermediate stock and incoming
batch quantity. Output for plants without a batch unit is byte-identical (the
golden hashes did not move). A plant restarted on the new image logs
`plant.snapshot_ignored` once (its version 2 snapshot), replays from its start
to the same history, and saves version 3 snapshots from then on.

**Live check (November 22, 2026, image `flow-data-simulator:2.0.0-dev-batch`).**
`PlanTestBatch220600` (chemicals base, 2-day backfill, anchor 06:00:27Z) went
live in under a minute. Historian matched the engine on 33,403 points (0
mismatches, 0 missing), and all 28 `batchphases` rows equaled the preview.
`R1.Temperature` lay on the Heat ramp for all six completed Heat phases
(maximum residual 0.42 °C). After `docker stop` (exit 130) and start, it logged
`plant.snapshot_resumed` and matched again. It was then removed (24 tables
dropped).

## Golden batch drift and batch quality

Phase 4 of the interview front end. It makes the golden-batch storylines 5a,
5b and 5c buildable.

- **Golden profile:** `recipes[].golden` (optional) holds `phaseMinutes` (by
  phase name; each defaults to the middle of the phase's range) and
  `yieldPercent` (`target` and `sigma`; default 95 and 0.5). The golden
  setpoints are the recipe's own ramps. Each batch draws its yield from its own
  random stream.
- **Deviation score:** for each phase, the running minutes' distance from the
  golden minutes in half-widths of the phase's range (1 = the edge of normal),
  or the temperature lag in units of 10 °C, whichever is larger. The batch
  score is the worst phase. A batch is `golden` when the score is at most
  `batch.goldenThreshold` (default 1). Without a drift every batch is golden,
  because phase times are drawn inside their ranges.
- **Drifts:** top-level `drifts[]`, each with `line` (a batch line),
  optional `recipe`, `phase`, `cause`, `from`, `until` and/or `endsWith`,
  `rampDays` and `effect` (`phaseMinutesFactor`, `temperatureLagC`,
  `yieldDelta`).
  - The drift's strength rises linearly from 0 at `from` to 1 after
    `rampDays`. It drops to 0 at `until`, or when the preventive task
    `endsWith` (on the same line) first completes after `from`.
  - A batch takes the strength at its start. Its phase runs `factor` times
    longer, its temperature trails the setpoint by up to `lag` °C at mid-phase
    (back on the ramp at the phase's end), and its yield moves by `yieldDelta`.
  - Drifts are top level so storyline fragments can add them. `drifts` and
    `maintenance.preventive` are additive in composition, like incidents.
- **Batch release testing:** `quality.batchRelease` with `labMinutes` and
  `parameters[]` (`name` of a quality parameter, optional `lower` and `upper`
  release limits, optional `yieldSensitivity`).
  - Each discharged batch waits in `Quarantine` and is sampled. The sample row
    has `batch_id` set and `lot_id` empty; results arrive after `labMinutes`.
  - A result is the parameter's target plus the SKU offset plus
    `yieldSensitivity` × (yield − golden yield) plus noise. Out of limits, the
    batch goes `On Hold` (a `qualityholds` row with `batch_id`) for
    `quality.holdMinutes`, then is released.
  - Released intermediates go into stock for the fed line. A fed order inherits
    each batch's yield shift in proportion to what it took. Its lot's lab result
    and the line's analyzer tag both include that shift, so a drifted syrup that
    passed its wide release limits can fail the finished-drink spec at the
    filler.
  - A direct-sale batch order's lot has no sample of its own. It is released
    when its last batch is.
- **Deviations (GxP):** `quality.deviations` with `closeHours` needs
  `quality.batchRelease` and `people.qa` (1–3 QA people with `id`, `name`,
  `shift`; written to `employees` with role `QA`).
  - Every batch that is not golden gets a `deviations` row at discharge. The
    row names the worst phase and what was seen.
  - The QA person on shift closes it `closeHours` later (`closed_utc`,
    `qa_approver_id`). Only then can the batch be released.
- **Tables:** `batches` gains `yield_percent`, `deviation_score`, `golden` and
  `released_utc`. A new table, `goldenprofiles`, holds one row per recipe
  phase with `golden_minutes`, `setpoints_json` and `golden_yield_percent`.
  `deviations` exists only with `quality.deviations`. In batch plants,
  `qualitysamples` and `qualityholds` gain `batch_id`; other plants keep their
  columns.
- **Tags per batch line:** `BatchDeviation` is the worst phase score so far,
  updated at the end of each phase, so after discharge it reads the batch's
  `deviation_score`. `HeatRate` is °C per minute while the temperature is
  rising, and 0 otherwise.
- **Connect spec and Atlas starter:** `Recipe` instances, the query properties
  `Line.BatchesOffGolden` and `Recipe.GoldenComparison` (phase-by-phase running
  minutes against golden minutes for the recipe's last 10 batches), and, with
  deviations, `Line.BatchDeviations` (the audit trail with the approver's name
  and the batch's release time).
- **Preview:** `summary.batches.comparison` (one row per discharged batch:
  cycle minutes, yield, deviation score, golden, status) and
  `summary.batches.drifts` (each window, ended early by its cleaning PM when
  that happened). The review page's batch comparison table (rows started
  inside a drift window shaded) is on `feature/phase-4-golden-preview`, built on
  phase 7's shared renderer.
- **Validation errors:** `plant.invalid_drift`, `plant.invalid_drift_time`,
  `plant.invalid_golden`, `plant.unknown_phase`, `plant.no_batch_line`,
  `plant.qa_required`, `plant.batch_release_required` and `plant.unused_qa`.
- **Bases:** every recipe in the three batch bases has a golden yield. The pharma
  base tests each granulation batch (assay depends on yield). The syrup base
  now has a Quality area: finished-drink Brix 10.7–11.3 at the filler, and
  syrup released at 10.4–11.6.

**Storylines:**

| Storyline | Base | Drift | What it shows |
| --- | --- | --- | --- |
| `golden-batch-reactor-drift` (5a) | `chemicals-batch`, 14-day history | Heat ×1.35, lag 12 °C, yield −4 from −9 days, ramp 4 days | Ended by the jacket cleaning PM `PM-JK101-CLEAN` (every 11 days, so about 2 days before the anchor) |
| `golden-batch-syrup-holds` (5b) | `beverage-syrup-batch`, 10-day history | Dissolve ×1.3 and yield −4, Blend ×1.25, −7 to −3 days | Filler lots made from drifted syrup fail Brix and go on hold; genealogy traces each to its syrup batches. Drifted syrup still in stock causes holds after the drift ends |
| `golden-batch-pharma-deviation` (5c) | `pharma-batch`, 10-day history | WetGranulate ×1.4 and yield −4, −8 to −3 days | Off-golden batches get deviations, and QA closes each before release |

**`EngineVersion` 4** adds the snapshot state for this: per-batch drift effects
and yield, the line's process temperature and BatchDeviation value, drift ends,
batches waiting for release, and lots waiting for their batches. Packaging
plants' output is byte-identical (the golden hashes did not move). Batch plants'
history changes: new columns, gradual reheat, and quarantine where release
testing is configured.

**Live check (November 22, 2026, image `flow-data-simulator:2.0.0-dev-golden`).**
Each storyline ran alone in Docker, at a 30-second sample interval against the
Test Dataset (7-day purge age, so the check covers the last 6.8 days). Each was
removed afterwards.

- **`PlanTestGoldenA0629` (5a):** Historian matched the engine on 116,844
  points (0 mismatches, 0 missing).
  - `batches` by start: deviation scores rose above 1 from 11-14 (to 2.2) with
    yields near 90 %. After the jacket clean completed at 11-19 22:19, all 6
    later batches were golden at about 94 %.
  - Mean `HeatRate` during Heat was 1.43–1.45 °C/min in the drift and
    2.1–2.5 °C/min after the clean.
- **`PlanTestGoldenB0631` (5b):** 0 mismatches on 120,364 points. All 10 held
  filler lots traced through `batchconsumption` to syrup batches made inside
  the drift window. No syrup batch was held.
- **`PlanTestGoldenC0631` (5c):** 0 mismatches on 123,455 points.
  - All 12 off-golden batches had a deviation, each closed by a named QA
    approver, and none was released before its closure.
  - `Line.BatchesOffGolden`, `Recipe.GoldenComparison` and
    `Line.BatchDeviations` ran on the live tables.

## Connect mapping spec and Atlas starter (milestone 6)

```
plant connect-spec <model> <output-folder> [anchorUtc]
```

This writes two files:

- **`connect-spec.md`:** connection settings (where the read-only password is
  kept, never the password itself), the polling rules, and for every table its
  columns, key, references and incremental query.
- **`connect-spec.json`:** the same information as JSON, plus an **Atlas
  starter**:
  - the asset hierarchy as instances: site, lines, equipment, products and
    people;
  - every tag as a separate **Sensor** node bound to its Historian tag;
  - transactional data as **query-backed properties**: open tickets,
    maintenance history and downtime per equipment; the current order and runs
    per line; quality holds; open shipments.

The starter is input for Atlas modeling, not a compiled payload. Work orders
are referenced through queries, not created as instances (owner preference).
Example: `examples/plants/bluebird-bev-connect/`.

## The interview (milestone 4)

The Claude skill `skills/flow-data-simulator/SKILL.md` interviews the user about
the story, the plant, the people, demand, maintenance and areas, writes the
model, validates it, and renders a preview page with
`scripts/render_plant_preview.py` (template `tools/plant-preview/index.html`).
It asks for approval before any build or run.

## Reaching Claude through Pulse Gage (web interview)

The web interview reaches Claude through Timebase Pulse's Gage proxy
(`POST /api/gage/proxy/v1/messages`), with the official Anthropic C# SDK. The
user enters no model key: the LLM provider the customer already approved for
Gage is reused, and its key never leaves Pulse. `GageClient`
(`src/IndustrialDataSim.Runtime/GageClient.cs`) holds the proven SDK settings.

```
plant gage-check
```

It runs the full chain (Pulse token, `GET /api/gage/models`, then a
one-sentence test message) and prints the model handle, the reply and the
token usage. Pulse meters the call under the client's policy, as usual.
Settings: `TIMEBASE_PULSE_URL`, `TIMEBASE_CLIENT_ID`, `TIMEBASE_CLIENT_SECRET`
and `TIMEBASE_CA_BUNDLE` (the same Pulse client the Historian writer uses), plus
`FLOWSIM_GAGE_MODEL` (default `anthropic/claude`).

**One-time Pulse setup:** the Pulse client needs `Pulse` among its allowed
audiences (Pulse → Clients → the client → Allowed Audiences). A client that only
has `Historian` gets tokens that Gage rejects with "The audience '(null)' is
invalid". Give the client a Soft policy in Pulse → Policy → Client so the
interview is metered but never refused. The usage page (Pulse → Gage → Usage,
`GET /api/gage/usage`) is for administrators; the client itself gets HTTP 403
there.

When no model is reachable, it explains what to fix and stops. There is no
direct-key fallback, and nothing is retried.

| Code | Meaning and fix |
| --- | --- |
| `gage.pulse_unreachable` | DNS, TCP or TLS failure: check `TIMEBASE_PULSE_URL`, the network and the CA bundle |
| `gage.client_rejected` | Pulse refused the client credentials (fix the client or secret in Pulse → Clients), or Gage refused the token (add `Pulse` to the client's allowed audiences in Pulse → Clients) |
| `gage.no_models` | Empty catalog: add a provider in Pulse → Gage → Providers |
| `gage.model_missing` | The handle is not in the catalog: the message lists the available handles; set `FLOWSIM_GAGE_MODEL` |
| `gage.no_key` | The provider refused Pulse's credentials or reported a missing key: set the key in Pulse → Gage → Providers |
| `gage.blocked` | HTTP 403 (a Block policy or a model allow-list): ask the Pulse admin to allow the client |
| `gage.throttled` | HTTP 429 from a Throttle policy: the message includes Pulse's wait time and reason |
| `gage.not_licensed` | Pulse's licence does not include Gage |
| `gage.timeout`, `gage.stream_error`, `gage.http_status`, `gage.invalid_response` | Other failures, each with the operation and Pulse's sanitized reason |

The client protects the Pulse token. It sends requests only to
`TIMEBASE_PULSE_URL`, strips headers outside a small allowlist (the SDK would
add `ANTHROPIC_CUSTOM_HEADERS`), and refuses any credential other than the Pulse
token, so an ambient `ANTHROPIC_API_KEY` or `ANTHROPIC_AUTH_TOKEN` is never
sent. Error text from Pulse is bounded, stripped of control characters, and
token-shaped strings are redacted.

## The interview service (web interview, headless)

`src/IndustrialDataSim.Interview` is the need-first interview as a tool loop
the app owns. The web page (phase 7) puts a chat around it. It is kept out of
Core, so Core has no LLM dependency.

- **Prompt:** `InterviewPrompt` combines `examples/storylines/interview.md`, a
  summary of the library (ids, titles, pitches, industries, roles, KPIs,
  evidence, engine status, closest) and the plant limits. The text is the same
  for every session. The date and interview id go in the first user turn.
- **Tools (strict schemas):**
  - `record_audience` saves the needs profile.
  - `find_storylines` ranks the library deterministically in code
    (`StorylineRanking`): industry 4 points, each matching role 2, each
    matching KPI 2, each keyword 2. An unavailable storyline carries
    `offerInstead`: its `closest` entry, or the best available match when
    `closest` is null.
  - `compose_model` runs `PlantStorylines.Compose` with the overrides, applies
    renames, then `PlantModelLoader`. Errors come back with field paths.
  - `preview_plant` runs the engine and stores the full preview (tags sampled
    every 5 minutes, about 2 MB for a week). It returns the headline numbers
    and each storyline's moments: warning start, failure, tickets, holds and
    uncertified assignments.
  - **There is no build or run tool, and a test asserts it.** Approval is the
    user's click in the page.
- **Loop:** `InterviewSession` calls `GageClient.CreateMessage` with
  `max_tokens` 16000, no `tool_choice` and no prefill. It handles each case
  as follows:
  - Malformed tool input returns an `is_error` result.
  - A turn may use at most 12 tool rounds.
  - `max_tokens` continues once.
  - A `refusal` is shown plainly and not replayed.
  - A `gage.*` failure ends the turn with an "explain and stop" event.
- **Persistence:** simulator-owned tables in `flow_data_sim`, created on first
  use like `flowsim_snapshots`:
  - `flowsim_interviews`: status, needs, draft model and preview;
  - `flowsim_interview_messages`: append-only history with token usage per
    reply.

  Messages are stored as text and replayed byte for byte. When a process is
  killed mid-turn, the interview is left `in_turn`. Resuming it re-runs any
  unanswered tool calls (they are idempotent), or asks the model again.

Probe for live runs (`tools/IndustrialDataSim.InterviewProbe`). It uses the
same environment as the simulator container, plus `FLOWSIM_PG_CONNECTION`:

```
InterviewProbe --library examples/storylines --script tools/IndustrialDataSim.InterviewProbe/scripts/bottler-late-orders.txt
InterviewProbe --script <same file> --resume <interview id>   # skips lines already sent
```

Measured cost through Gage (`anthropic/claude`, Claude Sonnet 4.6), November
22, 2026: a 3-turn bottler interview to a valid preview used about 64,000 to
88,000 input tokens and 2,700 to 3,400 output tokens. Most of the input is the
system prompt and tools, about 6,000 tokens, resent on every call. Pulse
reported no cache reads. The SDK request carries no `cache_control`, so caching
would need a change to `GageMessageRequest`.

## Storyline library and composing a model

`examples/storylines/storylines.json` holds the demo storylines, taken from
the customer needs analysis
(`docs/11212026_interview-front-end/customer-needs-analysis.md`). Each one
records:

- who it is for: industries, roles and KPIs;
- how strong the evidence is: the number of customer meetings;
- whether the engine supports it: `available`, `planned` or `later`;
- a model fragment for a base plant (`examples/storylines/bases/`);
- a talk track for the demo.

The interview script (`interview.md`) and the demo-guide template
(`demo-guide.md`) sit next to it.

```
plant compose <storylines-folder> <output-model.json> <SimulationName> <storylineId>...
```

`compose` applies each fragment to the base as a JSON merge patch, then sets
the simulation name, validates the result and writes the model file.

- **Planted events add up:** the `incidents` and `quality.excursions` lists
  combine, so storylines can be stacked.
- **Conflicts are reported:** any other value that two storylines set
  differently is reported as `storyline.conflict`, with both ids and the path.
- **Unavailable storylines are refused:** `storyline.not_available`.

## How the loop works

1. **Demand:** sales orders arrive at `demand.ordersPerDay`, each with a random
   SKU, customer, size and due date. The run starts with about one day of open
   orders.
2. **Production orders:** each sales order becomes a production order on the
   capable line with the least queued work.
3. **Scheduling:** each idle line takes its earliest-due order. A SKU change
   costs `changeoverMinutes`, recorded as planned downtime.
4. **Breakdowns:** while running, each failure mode accumulates run time until a
   breakdown, drawn from an exponential distribution with mean `mtbfRunHours`.
   Short minor stops happen at `minorStops.perRunHour`. The operator clears them,
   and they never reach the detector.
5. **Detection:** after a breakdown the line's speed is zero. After
   `detectionMinutes` the detector records unplanned downtime and opens a
   corrective ticket with the failure mode's priority.
6. **Repair:** the ticket goes to a free technician who is on shift and holds
   the skill: highest priority first, then oldest. With nobody eligible, it waits
   for a technician to finish or for the next shift.
7. **Ticket lifecycle:** Open → Assigned → In Progress (after the response time)
   → Complete (after the repair time). Parts are used, and the line resumes its
   interrupted order.
8. **Preventive maintenance** comes due every `everyDays` at `startHour` UTC. It
   starts when the current order ends and holds the line in Maintenance.
9. **Planted incidents** force a named failure at the given time, or as soon as
   the line next runs.

## What the tags show

- **Each line:** `State`, `SKU`, `OrderNumber`, `Speed`, `GoodCount` and
  `RejectCount`. A batch line has `BatchId`, `Phase`, `Temperature`,
  `Pressure`, `AgitatorSpeed` and `Level` instead of the speed and counts.
- **Each unit:** `Status` and `Vibration`; fillers also have `FillVolume`.

Every tag is computed from the same run:

- **Speed** is zero for exactly the time the line is Down, which is the
  detector's rule.
- **Counts** integrate the running time.
- **Vibration** rises over `warningHours` before each failure of that unit, and
  a filler's fill volume drifts low, so the signals foreshadow the ticket.
- **Noise** is stateless: any time window reproduces exactly.

## Tables

Purposes are `lines`, `equipment`, `skus`, `shifts`, `customers`, `employees`,
`certifications`, `parts`, `failurecodes`, `salesorders`, `productionorders`,
`productionruns`, `downtimeevents`, `maintenancetickets`,
`maintenanceticketstatus` and `partsusage`, plus the optional areas' tables
(Quality, Shipping, Certification and batch units above). The physical name is
`simulationname_purpose` in lower case.

## Limits (owner decision: keep plants small)

| Item | Limit |
| --- | --- |
| Lines (a batch unit counts as one) | 2 |
| Units per line | 6 |
| SKUs | 6 |
| Operators | 8 |
| Technicians | 4 |
| Customers | 6 |
| Parts | 20 |
| Failure modes | 12 |
| Preventive plans | 6 |
| Planted incidents | 10 |

Further rules:

- Shifts must cover the 24 UTC hours exactly once.
- Every SKU must be makeable on some line.
- Every failure or PM skill must belong to a technician.
- Minor stops must be shorter than the detection time.

## Not yet modeled

- **Mixing and CIP on packaging lines:** model them as a batch unit feeding
  the line (`beverage-syrup-batch`). On a packaging line alone they are still
  part of the changeover block.
- **Yield and quantities:** a batch's yield is recorded and drives its quality
  results, but the discharged quantity is still the batch size. Low yield does
  not make a fed line wait longer.
- **Suppliers, purchase orders and raw-material lots:** quality excursions
  stand in for supplier effects.
- **Utilities correlated with the process,** and multiple sites.
- **Sleep and power-cycle acceptance:** catch-up after a clock jump is covered
  by tests with a simulated clock; a real laptop sleep and a hard power loss
  in Docker are still to be run.
