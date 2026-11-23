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

## The web page comes first when it is installed

The web container `flow-data-simulator-web` runs the same interview, library
and engine as a page in the Timebase stack, with no Claude Code needed. Check
for it before interviewing here:

```bash
curl -s http://localhost:8480/api/health
```

- **It answers:** point the user to <http://localhost:8480>. The page
  interviews them (Claude through Pulse Gage; they can attach files), shows
  the plan on **Review plan**, builds it only on their **Approve plan** click,
  hosts it inside the container (surviving restarts), and offers the demo
  guide, Connect spec, Atlas starter and Gage kit per plant. To change a page
  plant, they ask the interviewer there; approving the change rebuilds it in
  place after a double confirmation. Each plant approved there gets its own Historian
  Dataset, named after it, with a 365-day purge age. Plants approved there are managed there: stop, start and
  remove them on its Plants tab, never with `plant run` or `plant remove` here.
  Opening a plant there (its own tab) shows its Summary, trends from its own
  Dataset and its Postgres tables, read-only; point the user there to look
  inside a page-built plant instead of querying it by hand.
- **It does not answer:** offer the setup (`deployment/web/README.md` in the
  repository: Pulse client with the `Pulse` and `Historian` audiences, a Soft
  client policy, the Gage model, `plant gage-check`),
  or carry on with the CLI path below.
- **Use the CLI path below** when the page is not installed, when the user
  wants Claude Code to drive, or for what the page does not do: hand-written
  models, edits to a running plant, re-anchoring.

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

## 1. Interview: need first

Follow the shared script `examples/storylines/interview.md`, the same one the
web interview uses. In short:

1. **Audience:** roles, industry and product, and the one or two problems that
   cost them the most, with a recent example.
2. **Their systems:** ERP and CMMS (SAP and SAP PM are common), MES or LIMS,
   historian and SCADA (Ignition is common).
3. **Constraints:** sensitivity to AI and cloud; the history must fit the
   Dataset purge age. If the demo is days away, run the plant continuously or
   rebuild it nearer the day: its times are relative to now.
4. **Storylines:** propose 1–3 from `examples/storylines/storylines.json`.
   They come from 43 real customer meetings. Rank by industry, then roles,
   then KPIs, and explain each with its `pitch`. Only `available` storylines
   can be built. For `planned` or `later` ones, say "not in the simulator
   yet", offer the storyline in its `closest` field, and record the request
   in the demo guide. Chosen storylines must share a base plant; some run on
   several (`base` plus `variants`), and `plant compose` picks one they all
   run on. Keep a storyline's own `timeline.backfill` (5a: 14 days, 5b: 10
   days): a shorter history loses its baseline or its recovery. When that is
   longer than the Dataset's purge age, say so: Historian drops the oldest
   points, Postgres keeps every row.
5. **Plant:** names that feel like theirs (simulation name in PascalCase, for
   example `AcmeNuggs`; site, line and product names). Change rates or staff
   only if the story requires it. Keep plants small.

The stages are a checklist, not a strict order: follow the user's lead, but
cover all of them before composing. The script (`interview.md`) has the details:
evidence counts, mapping the user's words to library ids, and the `closest`
rule.

Use the question tool when it is available, with a recommended default first.
Never use real people's names unless the user gives them.

## 2. Compose and validate the model

Compose the model from the library, then validate it:

```bash
dotnet run --project src/IndustrialDataSim.Cli -- plant compose examples/storylines ~/flow-data-simulator/models/<Simulation>.json <Simulation> <storylineId> [<storylineId> ...]
docker run --rm -v ~/flow-data-simulator/models:/models:ro flow-data-simulator:2.0.0-dev plant validate /models/<Simulation>.json
```

- **Running compose in Docker:** mount the library with
  `-v <repo>/examples/storylines:/storylines:ro` and pass `/storylines`.
- **Adjusting the composed model:** edit the file afterwards for names, people
  or tuning. Use `examples/plants/bluebird-bev.json` as the field reference.
  Every field is required unless the docs say otherwise, and unknown fields
  are rejected.
- **Errors:** every reported error names its field path and the correction;
  fix them all.
- **A plant no storyline fits:** write the model by hand from the Bluebird
  example.

### Tuning the story (do the arithmetic, then check it in the preview)

- **Breakdowns per week** for a mode ≈ (run hours per week) / `mtbfRunHours`.
  A line running about 65% of the week runs about 110 hours.
- **Line capacity in orders per day** ≈ 1440 × (share of the day available) /
  (`changeoverMinutes` + cases per order / `casesPerMinute`). Set
  `ordersPerDay` to about 80–90% of total capacity for a busy plant.
- **Late orders** need tight due dates (`dueDays` around 1–2.5). Breakdowns on
  a line only make orders late when the other line cannot absorb the work.
- **Warning hours:** vibration (and fill volume, on fillers) drifts over this
  period before the failure. Use 6–12 h for a predictive-maintenance story, and
  0 for sudden failures.
- **Minor stops** must be shorter than `detectionMinutes`. Anything longer is a
  breakdown and needs a failure mode.

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
- **The demo guide:** fill `examples/storylines/demo-guide.md` from the
  interview's audience notes and each chosen storyline's `talkTrack`. Use the
  real tag root and table prefix, and the times of each storyline's moment
  from the preview. Give it to the user as a page or file. It is what makes
  the demo mean something to the audience.

## Changing a running plant

- **Re-anchor or change the model:** stop the container, then
  `plant remove <Simulation> --confirm`, which drops only that simulation's
  tables, then build and run again. Tags already in Historian stay.
- **Small changes:** use a new simulation name (for example `BluebirdBev2`)
  rather than editing a model in place. A stored model that no longer
  reproduces its history stops with `plant.history_mismatch`.
