# Plant simulation

Design and owner decisions: [design-plant-simulation.md](design-plant-simulation.md).
Example: [examples/plants/bluebird-bev.json](../examples/plants/bluebird-bev.json),
a rebuild of the Bluebird Bev demo.

It simulates the **Production → Maintenance loop**. Milestone 1 is the offline
engine and preview; milestone 2 writes the records to Postgres
(`flow_data_sim`); milestone 3 runs a plant live in Docker, writing rows to
Postgres and tags to Historian.

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

- **Mixing and CIP:** these phases are part of the changeover block.
- **Other areas:** quality, warehouse, shipping, suppliers and lots come with
  later milestones.
- **Outputs and interview:** the Postgres output, the live run and the interview
  skill are milestones 2–4.
