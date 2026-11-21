---
name: flow-data-simulator
description: Interview the user about what a Timebase demo or PoC should show, then build and run a matching simulated plant with the Flow Data Simulator, where transactions (orders, maintenance tickets, quality results, shipments) and time-series tags come from one clock and stay aligned. Use when the user wants to create, preview, build, run, extend, re-anchor or remove a demo plant, manufacturing simulation, synthetic plant data, or simulated SQL records plus Historian tags for Timebase, for example "build a demo plant for a bottling customer", "simulate a factory with breakdowns and work orders", "add a quality area to the Bluebird sim", "move the demo to next Tuesday", or "spin up simulated data for Atlas and Connect".
---

# Flow Data Simulator: plant interview and build

The Flow Data Simulator runs a small plant on one deterministic clock:

- **Production:** sales orders become production orders, then a line schedule
  (changeovers, runs, minor stops).
- **Maintenance:** breakdowns show in the signals first, then a detector raises
  a downtime event and a maintenance ticket. The line stays down until a
  technician completes the ticket.
- **Quality, if enabled:** a sample per finished run; out-of-spec results put
  the lot on hold.
- **Shipping, if enabled:** released lots ship against their sales orders, and
  holds delay shipments.

Rows go live to Postgres (`flow_data_sim`, tables `simulationname_purpose`), and
tags go to Timebase Historian, both from the same run. Your job is to turn the
user's story into a plant model, show a preview, and build and run it only after
they approve.

Repository: the `industrial-data-sim` working copy (this skill lives in its
`skills/` folder). Reference docs there:

- `docs/plant-simulation.md`: model fields, commands, Postgres and live run.
- `docs/design-plant-simulation.md`: design and owner decisions.
- `examples/plants/bluebird-bev.json`: a complete worked example.

## Rules (owner decisions; do not relax them)

- **Keep plants small:** at most 2 lines, few units, the minimum staff that
  tells the story, and few SKUs. The validator enforces the limits.
- **One shared Postgres database,** `flow_data_sim`, reused by every simulation.
  Table names are lower case `simulationname_purpose`. A simulation only ever
  touches its own prefix. Never drop, edit or write tables of another
  simulation, or anything the simulator did not create.
- **Historian writes go only to the Dataset in the deployment env file.**
  Never write, reconfigure or delete other Datasets.
- **Always show the preview and get the user's approval before `plant build`
  or `plant run`.**
- **Stop the plant with `docker stop`.** The container's stop signal is SIGINT;
  never use `docker kill`.
- **Never print or commit secrets.** The env files in `~/flow-data-simulator/config` are private.

## 1. Interview

Ask in plain language, a few questions at a time. Use the question tool when it
is available, with a recommended default first. Skip anything the user already
told you. Cover:

1. **Story:** the industry and product, and what the demo must reveal. Push for
   1–3 concrete storylines ("a labeler that keeps jamming and makes orders
   late", "a supplier switch that causes quality holds").
2. **Plant:**
   - 1–2 lines, and what each line can make;
   - 3–6 key units per line, with a class each (filler, capper, labeler, packer,
     palletizer, conveyor, mixer, other);
   - SKUs with rates (cases a minute), fill size and unit, and reject %;
   - changeover minutes, including cleaning;
   - shifts (UTC hours covering the day exactly once).
3. **People:** operators per line and shift, and technicians with skills
   (mechanical, electrical …), as few as the story allows. Generate realistic,
   diverse names. Never use real people's names unless the user gives them.
4. **Demand:** orders per day, cases per order, due-date window, and 2–4
   customers.
5. **Maintenance:**
   - failure modes per unit: MTBF in run hours, repair minutes, skill, priority,
     the hours of warning the signals give, and parts;
   - preventive maintenance;
   - minor stops;
   - detection minutes.
6. **Storylines to plant:** failure modes at specific times relative to now (`"at": "-30h"`).
7. **Areas:** Production and Maintenance are always on. Ask whether Quality
   (samples, specs, holds) and Shipping (lots, inventory, shipments) should be
   included.
8. **Timeline and output:** backfill (`"7d"`, `"14d"` …), open-ended (default)
   or an end time; the simulation name (PascalCase, for example `AcmeNuggs`),
   which becomes the table prefix and tag root; and the sample interval
   (10 000 ms by default).

### Tuning the story (do the arithmetic, then check it in the preview)

- **Breakdowns per week** for a mode ≈ (run hours per week) / `mtbfRunHours`.
  A line running about 65% of the week runs about 110 hours.
- **Line capacity in orders per day** ≈ 1440 × (share of the day available) /
  (`changeoverMinutes` + cases per order / `casesPerMinute`). Set
  `ordersPerDay` to about 80–90% of total capacity for a busy plant. Go above
  that only if the story wants a backlog.
- **Late orders** need tight due dates (`dueDays` around 1–2.5). Breakdowns on a
  line only make orders late when the other line cannot absorb the work.
- **Warning hours:** vibration (and fill volume, on fillers) drifts over this
  period before the failure. Use 6–12 h for a predictive-maintenance story, and
  0 for sudden failures.
- **Minor stops** must be shorter than `detectionMinutes`. Anything longer is a
  breakdown and needs a failure mode.

## 2. Write the model

Write the plant model JSON (schemaVersion 1, kind "plant") using
`examples/plants/bluebird-bev.json` as the template. Every field is required
unless the docs say otherwise; unknown fields are rejected. Save it next to the
user's other simulator files, for example
`~/flow-data-simulator/models/<Simulation>.json`.

Then validate it:

```bash
docker run --rm -v ~/flow-data-simulator/models:/models:ro flow-data-simulator:2.0.0-dev plant validate /models/<Simulation>.json
```

When working from the repository, `dotnet run --project src/IndustrialDataSim.Cli -- plant validate <file>` also works.
Fix every reported error: each one names its field path and the correction.

## 3. Preview and approval

Generate the preview, then render it as a page:

```bash
docker run --rm -v ~/flow-data-simulator/models:/models -v /tmp/plant-preview:/out flow-data-simulator:2.0.0-dev plant preview /models/<Simulation>.json /out/preview.json 24
python3 scripts/render_plant_preview.py /tmp/plant-preview/preview.json /tmp/plant-preview/page --title "<Plant name>" --note "<what to look at>"
```

Publish the page folder (`index.html` plus `page-data.json`) as an Artifact. Then
summarize for the user:

- the headline numbers: orders, on-time %, breakdowns, tickets, and how long
  each line spends in each state;
- whether every planted storyline shows up as intended, for example the
  vibration rise before the planted failure, and its ticket and downtime rows.

Ask the user to approve, or to tell you what to change. Iterate on the model
until they approve. Never build before approval.

## 4. Build and run

Once the user approves:

```bash
docker run --rm --network timebase-2-0-beta_default --env-file ~/flow-data-simulator/config/datasim.env \
  -v ~/flow-data-simulator/models:/models:ro flow-data-simulator:2.0.0-dev plant build /models/<Simulation>.json
docker run -d --name <simulation>-plant --restart unless-stopped --stop-signal SIGINT --stop-timeout 120 \
  --network timebase-2-0-beta_default --env-file ~/flow-data-simulator/config/datasim.env \
  -v ~/flow-data-simulator/certs:/certs:ro flow-data-simulator:2.0.0-dev plant run <Simulation>
```

- **Build** fixes the anchor ("now") and stores the model in Postgres. Every
  restart replays the identical history.
- **Run** backfills, then follows real time until stopped. Watch
  `docker logs <simulation>-plant` for `plant.live`.
- **Status:** `plant status <Simulation>` shows the progress.

Afterwards, give the user:

- the table names and the read-only Connect login: user `flowsim_reader`, host
  `flow-data-sim-pg`, port 5432, database `flow_data_sim`. The password is in
  `~/flow-data-simulator/config/postgres.env`; tell them where it is, never what
  it is;
- the tag root;
- the Connect mapping spec and the Atlas starter. Generate them from the
  approved model with the same anchor that `plant build` reported:

  ```bash
  docker run --rm -v ~/flow-data-simulator/models:/models:ro -v ~/flow-data-simulator/specs:/out flow-data-simulator:2.0.0-dev \
    plant connect-spec /models/<Simulation>.json /out/<Simulation> <anchorUtc>
  ```

  Summarize the spec, and offer to model the Atlas starter with the
  atlas-data-modeling skill. Sensors are separate nodes; tickets and other
  records are query-backed properties, not instances.

## Changing a running plant

- **Re-anchor or change the model:** stop the container, then
  `plant remove <Simulation> --confirm`, which drops only that simulation's
  tables, then build and run again. Tags already in Historian stay.
- **Small changes:** use a new simulation name (for example `BluebirdBev2`)
  rather than editing a model in place. A stored model that no longer
  reproduces its history stops with `plant.history_mismatch`.
