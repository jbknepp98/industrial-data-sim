# Design note: plant simulation (interview, areas, transaction loop)

Status: **approved with owner decisions**, November 21, 2026. No code has changed.
This is a new major capability. Its working name is **2.0**: it builds on 1.1.0
(open-ended sessions), which stays paused pending the owner soak.

## Why

Every Timebase demo since June (ACME Nuggs, McDowell's, ACME Steel, RFK Racing,
Bluebird Bev) has been built in the same way:

1. Pick a story.
2. Write a Simulator collector file for the tags.
3. Separately generate SQL records: orders, work orders, maintenance, staff,
   inventory.
4. Make the SQL Connect-friendly.
5. Model it in Atlas.
6. Roll everything forward before each demo.

The SQL and the time series were only ever lined up by hand. Bluebird is the one
case with true timestamp alignment, and a Collector restart broke even that.
Separate sequences don't correlate, dates go stale, and every project rebuilds
the link.

The owner's goal is a simulator that interviews the user about what to
demonstrate, then builds a factory in which:

- **Records drive signals.** Sales orders, production orders and schedules
  determine what the equipment does.
- **Signals drive records.** A downtime event raises a maintenance ticket.
- **The loop closes.** The ticket keeps the line down until it is completed,
  which delays orders.
- **Areas interact.** Production, Maintenance, Quality and Shipping each have
  their own records, events and tags, and the downstream areas depend on
  Production.

## Owner decisions (November 21, 2026)

1. **The front end is an interview with Claude:** a skill that drives the
   simulator's CLI. There is no web UI yet.
2. **Relational output goes to Postgres.**
3. **Live SQL (option A):** rows are inserted and updated when their simulated
   time arrives, so connectors see real changes. They are not pre-generated
   behind time-revealing views.
4. **First slice:** the Production → Maintenance loop. Quality, Warehouse and
   Shipping follow.

## Core idea: one plant, one clock

A single deterministic engine simulates the whole plant on one timeline.
Everything it emits, both SQL rows and tag values, comes from that one run, so
records and signals cannot drift apart. The same plant model, seed and anchor
always produce the same history. Re-anchoring for a new demo date means
regenerating, not hand-editing.

```
 plan & transactions ──► equipment state ──► time series (tags)
        ▲                                        │
        │                                  event detectors
        │                                        │
        └──────── new transactions ◄─────── events (downtime,
          (maintenance WO, holds, scrap)     quality excursion…)
```

The engine is **discrete-event at its core**, with continuous signals derived
from state:

- **Transactions:** orders, work orders and shipments are entities with
  lifecycles. Their state changes happen at scheduled event times.
- **Equipment state:** a state machine per line or unit, such as Running(SKU),
  Changeover, Idle, Down(reason) or Maintenance. Transitions are driven by the
  schedule, by injected failures and by work-order completion.
- **Signals:** each equipment class has templates that set speed, temperature,
  pressure, counts and quality from the current state and SKU, plus noise and
  drift. They reuse the 1.x generators and manufacturing nodes where possible.
- **Event detectors:** rules over signals and state, for example "speed below 5%
  of setpoint for 5 minutes while scheduled to run" or "fill weight outside
  limits for 3 consecutive samples". A detector emits a business event, which
  areas turn into transactions.
- **Deterministic ordering:** events at the same instant are ordered by area
  priority, then sequence. Randomness comes from named, seeded streams per
  entity, as in 1.x. A restart replays nothing and changes nothing.

## Areas

An area is a module with its own Postgres schema, entities, event rules and tags.
It subscribes to events from other areas. Areas can be switched on per demo.

| Area | Subscribes to | Owns (Postgres) | Emits tags | Feeds back |
| --- | --- | --- | --- | --- |
| **Production** | Demand, schedule, maintenance completions, quality holds | `prod`: sales orders, production orders, schedule, runs, batches/lots, downtime events, scrap | Line and equipment state, speeds, process values, good/reject counts | Downtime and fault events |
| **Maintenance** | Downtime and fault events, PM calendar | `maint`: work orders and status history, failure codes, technicians and certifications, parts, parts transactions | Optional: asset condition (vibration, motor current) | Line stays Down until the work order closes; PM windows block the schedule |
| **Quality** (later) | Batches | `qual`: samples, lab results, specs, holds and releases, NCRs | In-line checks (weight, moisture) | Holds block warehouse and shipping |
| **Warehouse** (later) | Released batches | `wh`: lots, locations, inventory transactions | — | Stock levels for shipping |
| **Shipping** (later) | Sales orders, inventory | `ship`: picks, loads, shipments, carriers | Dock and scale tags | Late shipments trace back through the loop |

Shared reference data (site, areas, lines, equipment, SKUs, BOM, shifts, people)
lives in the simulation's own reference tables, for example
`bluebirdbev_equipment` (see owner decisions, round 2).

### First slice: Production → Maintenance

**Production:**
- **Demand:** a generator produces sales orders (customers, SKUs, quantities,
  due dates). They become make-to-stock or make-to-order production orders.
- **Scheduling:** a simple scheduler sequences orders on lines with SKU rates
  and changeover times, and respects maintenance windows.
- **Running:** the line state follows the schedule. Counts, batches and lots
  accumulate.

**Maintenance:**
- **Failures:** each equipment item has failure modes with MTBF and MTTR
  distributions, plus planted stories, for example "motor MOT-1007 degrades over
  10 days, current rises, then it trips".
- **Detection:** a detector raises a downtime event.
- **Work orders:** the maintenance rules open a corrective work order with
  priority, failure code and an assigned technician holding the right
  certification. The order moves through Open → Assigned → In Progress →
  Complete, with parts used along the way. The line returns to service when it
  completes.
- **Preventive maintenance:** PM work orders come from a calendar and occupy
  the line.

**The loop is visible.** A breakdown cuts output, the production order finishes
late, and the next order slips.

## Outputs

**Historian.** The existing 1.x adapter is used unchanged: durable batches,
blind publish, no replay. Tag names follow the ISA-style hierarchy and
descriptor conventions from past demos, for example `Site.Area.Line.Unit.Speed`.

**Postgres (new).** The schemas follow the `atlas-demo-database` rules, adapted
to Postgres:

- **Keys:** natural keys (`WO-2026-000101`) as single-column primary keys, with
  no serial IDs exposed.
- **Watermark:** a `row_version bigint` from one global sequence, set by trigger
  on every insert and update. `modified_utc` is for reading, not watermarking.
- **Soft deletes:** `is_deleted` and `deleted_utc`.
- **No clocks in views:** no `now()` in views a connector polls. Metrics use
  generated columns.
- **Access:** a read-only role for Connect.

**Live writes (option A).**
- **History:** at admission, the engine writes the backfill rows in bulk.
- **Live:** each row is inserted, or updated, when its simulated time arrives. A
  work order is inserted Open, then updated to Complete later, which is exactly
  what a polling connector should see.
- **No duplicates:** each SQL write is keyed by natural key and event sequence.
  The engine's durable cursor commits only after Postgres commits, and an
  idempotent upsert makes a retried write harmless. This is exactly-once
  *effect* for SQL, which is stronger than the Historian path can offer.

**Artifacts for the rest of Timebase:**
- a data dictionary and Connect mapping spec, generated from the plant model;
- optionally, a starter Atlas ontology and instance map.

## The interview (Claude skill)

The skill asks, in plain language:

1. What is the industry, and what story should the demo tell? Which problems
   should the data reveal?
2. What does the plant look like: lines, key equipment, SKUs with rates and
   fill sizes, shifts?
3. Which areas are in scope? The first slice offers Production and Maintenance.
4. What should demand and storylines look like (order volume, make-to-order vs
   make-to-stock mix), and what planted incidents with timing?
5. Backfill length, run open-ended or to a date, Postgres and Historian
   targets, and the tag and record naming conventions.

It then:

1. Writes a **plant model** file: declarative and versioned, like 1.x models.
2. Runs `plant validate`.
3. Runs `plant preview`: an offline run producing an HTML page with a Gantt
   chart of orders and work orders, line state, sample trends, record counts,
   and the planted incidents marked.
4. **Shows the preview for approval.** The owner always approves before anything
   is written.
5. Runs `plant build`, which creates the Postgres schemas and admits the plant
   to the runtime.
6. Runs the plant in Docker, as with 1.x.

Re-running the skill on an existing plant supports "add a Quality area" or
"move the demo to next Tuesday".

## Runtime and compatibility

- **Engine:** a new plant engine sits above the 1.x durable runtime. 1.x
  sessions keep working unchanged, and a plant compiles into internal tag
  streams plus a transaction stream.
- **Determinism and scale:**
  - every event and value is reproducible from model, seed and anchor, which
    makes re-anchoring a regeneration;
  - guards bound entity counts, events per hour and tags per area, set from
    measured capacity as in 1.x Phase 2;
  - the engine runs open-ended, with backfill then live, using 1.1.0's
    wall-clock guard.
- **Restarts:** the durable state holds the plant checkpoint (open orders, line
  states, random-stream positions), the Historian cursor and the Postgres
  cursor. Stopping the container with SIGINT and restarting resumes without
  shifting anything. Collector-style restart drift cannot happen.

## Out of scope for the first slice

Quality, Warehouse and Shipping areas; utilities correlated with process; a web
UI; SQL Server output; Atlas model generation beyond a starter; multi-site
plants; editing a running plant (change it by regenerating).

## Milestones

1. **Plant model and offline engine:** Production and Maintenance, deterministic
   tests (same seed gives identical records and tags), the loop proven in
   preview. `plant validate` / `plant preview`.
2. **Postgres writer:** schema DDL, triggers, the read-only role, live
   insert/update with exactly-once effect; crash tests at every commit boundary.
3. **Live run:** Historian and Postgres together in Docker; Connect polls the
   tables, and timestamps line up exactly with the tags.
4. **Interview skill:** produces the plant model, preview and build; dry-run it
   on a past demo, such as rebuilding the ACME Nuggs story.
5. **Quality area,** then **Warehouse and Shipping.**
6. **Generated Connect mapping spec and starter Atlas model.**

Each milestone ends with an owner-reviewed preview or live check, as in 1.x.

## Owner decisions, round 2 (November 21, 2026)

1. **One shared Postgres database.** It is a container on the Timebase Docker
   network, and every simulation reuses the database `flow_data_sim`.
2. **Table names are `simulationname_tablepurpose` in lowercase,** for example
   `acmenuggs_maintenancetickets` or `bluebirdbev_productionorders`. This
   replaces the per-area schemas (`prod`, `maint`, …) described above: the area
   is expressed in the table purpose instead. It overrides the
   `atlas-demo-database` "schemas rather than prefixes" rule for this tool.
3. **Simple rule-based scheduler,** with no optimization.
4. **People are generated from the interview:** technicians, operators,
   certifications.
5. **First proving story:** rebuild **Bluebird Bev** and compare it with the
   hand-built version.
6. **Keep simulations small for now:** at most **2 lines**, the minimum staff to
   tell the story, few SKUs. Validation enforces these limits so a model cannot
   quietly grow.

Consequences for the design:

- **Ownership in a shared database.** A simulation only ever creates, writes or
  drops tables carrying its own `SimulationName_` prefix. It checks that no
  other simulation already owns the name before creating anything, which
  mirrors the 1.x tag-ownership rule and the rule of never touching Datasets it
  did not create. Removing a simulation drops only its own tables, and only
  after confirmation.
- **The row-version sequence and the read-only Connect role** are shared across
  the database. Each simulation's tables get their own trigger.
- **Identifier case (owner decision):** table names are **lowercase**,
  `simulationname_tablepurpose`, for example `acmenuggs_maintenancetickets`.
  Postgres needs no quoting, and Connect queries stay simple. Column names are
  lowercase snake_case too.

## Open questions for the owner (answered above)

1. **Postgres location:** a container in the same Docker network as Timebase
   (recommended), or an existing server?
2. **Schedule realism:** is a simple rule-based scheduler enough to start, with
   no optimization?
3. **People:** generate technicians, operators and certifications from the
   interview, or reuse a standard roster?
4. **First proving story:** rebuild an existing demo (ACME Nuggs or Bluebird)
   so the result can be compared with what was hand-built?
