# Plant simulation

Design and owner decisions: [design-plant-simulation.md](design-plant-simulation.md).
Example: [examples/plants/bluebird-bev.json](../examples/plants/bluebird-bev.json),
a rebuild of the Bluebird Bev demo.

It simulates a small plant on one clock:

- **Production → Maintenance loop:** always on.
- **Quality and Shipping areas:** optional.

The milestones so far:

1. Offline engine and preview.
2. Records written to Postgres (`flow_data_sim`).
3. Live run in Docker: rows to Postgres and tags to Historian.
4. The interview skill.
5. The Quality and Shipping areas.
6. The Connect mapping spec and the Atlas starter.

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
SIGINT; exit code 130). Each cycle replays the engine from the plant start
through now, plus a look-ahead of the longest `warningHours` + 1 hour. The
look-ahead means vibration already rises before a failure the engine will
produce. Only rows and tag samples at or before now are delivered.

- **Rows:** Postgres, exactly once (see above).
- **Tags:** Historian, in batches of at most 1000 points, written to the Dataset
  in `TIMEBASE_DATASET`. The publish intent (`sending_until_utc`) is recorded
  before each batch and cleared after it. A graceful stop always finishes the
  batch in flight. If the process is killed mid-batch, the next start skips that
  batch and counts it in `uncertain_batches`; it never resends it.
- **One host per simulation:** a Postgres advisory lock refuses a second host
  (`plant.host_running`).
- **Log:** the host prints one JSON line per change of condition:
  `plant.started`, `plant.backfilling` (every 10 minutes), `plant.live` and
  `plant.stopped`.

The env file passed with `--env-file` needs the `TIMEBASE_*` settings (Historian,
Pulse, client credentials, CA bundle and Dataset) and `FLOWSIM_PG_CONNECTION`
(host `flow-data-sim-pg`).

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
  `RejectCount`.
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
`maintenanceticketstatus` and `partsusage`. The physical name is
`simulationname_purpose` in lower case.

## Limits (owner decision: keep plants small)

| Item | Limit |
| --- | --- |
| Lines | 2 |
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

- **Mixing and CIP:** these phases are part of the changeover block, and there
  are no mix-tank process tags.
- **Suppliers, purchase orders and raw-material lots:** quality excursions
  stand in for supplier effects.
- **Utilities correlated with the process,** and multiple sites.
- **Replay cost:** each live cycle replays the engine from the plant start, so
  it grows with run length. That is fine for weeks to months of a small plant;
  checkpointing is future work.
