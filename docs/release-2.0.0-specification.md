# Version 2.0.0 release specification

Status: written November 22, 2026, after `v2.0.0-rc.1` (tag on `a7bceef`,
[CI run 44999977188](https://github.com/jbknepp98/industrial-data-sim/actions/runs/44999977188)).
It records what 2.0.0 is and what blocks its release. Gates and their evidence are in
the [acceptance matrix](release-2.0.0-acceptance.md). How each part works is in
[plant-simulation.md](plant-simulation.md); the reasons behind the decisions are in
[design-plant-simulation.md](design-plant-simulation.md).

2.0.0 adds a second product to the repository without changing the first. The 1.x
session simulator (TVQ sessions in SQLite, the `production` and `production-host`
commands) is unchanged; [release-1.0.0-specification.md](release-1.0.0-specification.md)
still governs it.

## Product and architecture

The plant simulator builds a small simulated plant and runs it on one clock, so
transactions and time series agree. Records go to Postgres, where Timebase Connect
reads them, and tags go to a Timebase Historian Dataset. Plants are made for Timebase
demos and proofs of concept: the plant tells a story ("labeler jams make orders late")
that the Explorer, Connect, Atlas and Gage steps of a demo guide then show.

| Part | Project | Responsibility |
| --- | --- | --- |
| Engine | `src/IndustrialDataSim.Core` (`Plant/`) | Deterministic plant model: demand, scheduling, breakdowns, maintenance, quality, shipping, certification, batch units, golden batch drift. Model loader and validation. Storyline library and composition. No I/O and no LLM dependency |
| Runtime | `src/IndustrialDataSim.Runtime` | `PlantStore` (Postgres registry, rows, snapshots), `PlantHost` (delivery cycles), `HistorianClient`, `GageClient` (Pulse Gage proxy) |
| Interview | `src/IndustrialDataSim.Interview` | The need-first interview as a tool loop over Gage, with sessions stored in Postgres |
| Web | `src/IndustrialDataSim.Web` (`flow-data-simulator-web`) | The interview page, approval, in-container hosting of approved plants, downloads, health |
| CLI | `src/IndustrialDataSim.Cli` (`plant …` commands) | Validate, preview, compose, build, sync, run, status, remove, connect-spec, gage-check |
| Skill | `skills/flow-data-simulator` | The Claude Code route to the same engine, for hand-written models |

The supported deployment is the web container in the customer's Timebase 2.0 Docker
stack, from `deployment/web/` (image built from source or loaded from a CI artifact).
The CLI and skill remain for hand-written models and diagnostics.

## Included behavior

- **Plant engine (`EngineVersion` 4):** up to two lines (packaging lines or batch units),
  with the Production → Maintenance loop always on and optional Quality, Shipping,
  certification compliance (technicians and operators), batch units with recipes and
  feeds, golden batch profiles, drifts, batch release testing and GxP deviations.
- **Determinism:** the same model, seed and anchor produce the same rows and tags, byte
  for byte, whether run from the start or resumed from any snapshot.
- **Postgres:** one registry (`flowsim_simulations`), snapshots (`flowsim_snapshots`),
  the interview tables (`flowsim_interviews`, `flowsim_interview_messages`), the web
  hosting table (`flowsim_web_plants`), and 16 or more tables per plant under its own
  prefix (29 for the largest acceptance plants). Every plant table has its natural key, `row_version` (the Connect watermark),
  `modified_utc`, `is_deleted` and `deleted_utc`.
- **Historian:** tags under the plant's tag root, written in batches of at most 1000
  points. A plant approved on the page writes to its own Dataset, named like the
  simulation and created at approval with a 365-day purge age; the page never writes
  to, or deletes, a Dataset it did not create. The CLI writes to `TIMEBASE_DATASET`.
- **Live delivery:** backfill, then live, from snapshots, with catch-up after any stop in
  steps of at most 6 hours of plant time.
- **Storyline library:** 15 storylines (`examples/storylines/storylines.json`): 8
  available and 7 later. Available ones compose onto one of four base plants
  (`examples/storylines/bases/`), some with variants for other bases.
- **Interview:** Claude through Pulse Gage with the client's own token; tools
  `record_audience`, `find_storylines`, `compose_model` and `preview_plant`, and, for
  plants the page built, `list_plants`, `describe_plant`, `propose_plant_action` (stop or
  start, confirmed by the user in the page) and `change_plant` (moves a change into the
  plant's own interview); no build, run or remove tool. Attached files (PDF, images,
  Excel, Word, text; 10 MB each, 5 per message, 20 per interview) are information, never
  instructions. *(Added after rc.2.)*
- **Web page:** Timebase Plant Simulator in the Timebase 2.0 look. Interview in two
  steps (chat; **Review plan** with storylines, needs, preview and the Gage kit),
  **Approve plan**, rebuild in place of a changed plant behind a double confirmation,
  plant list with Stop, Start and Remove, and per-plant downloads (Connect mapping
  spec, Atlas starter, demo guide, Gage kit: prompts, Connect brief, Atlas brief).
- **Plant browser:** each page-built plant opens to Summary, Time series (one master
  range, trend windows of up to 4 tags, at most 31 days, 1,000 points per tag) and Tables
  (200 rows a page, sort, one search box, deleted rows). Read-only: `READ ONLY`
  transactions with a 5-second timeout, identifiers from the schema only, values as
  parameters; only plants this page built are listed or readable. *(Added after rc.2.)*
- **Image:** `flow-data-simulator-web`, linux-arm64 and linux-x64, non-root, SIGINT
  stop, built-in health check, with the CLI inside for `plant gage-check`.

## Invariants that block release if violated

1. **Rows exactly once.** Rows and the registry's `applied_sequence` commit in one
   transaction. A crash at any point is continued exactly by the next cycle, with no gap
   and no duplicate.
2. **History never changes.** A stored model and anchor always reproduce the history
   already written. A model that does not stops with `plant.history_mismatch`. Engine
   changes that alter output bump `EngineVersion`; the golden hashes in
   `PlantSnapshotTests` pin the output.
3. **Tags equal the engine.** Every tag point in Historian equals the engine's value at
   that time. A batch interrupted by a kill is resent unchanged
   (`plant.interrupted_batch_resent`); this deliberately differs from the 1.x Uncertain
   rule because plant tags are deterministic.
4. **Ownership.** A simulation creates, writes and drops only tables with its own prefix
   and its own registry rows. Removal never touches another simulation's tables. The page
   writes tags only to a Dataset it created for that plant, and deletes that Dataset only
   when the user asks in the Remove dialog, or empties it (delete and create again, same
   name) when the user confirms a rebuild of that plant.
5. **One writer per simulation.** A Postgres advisory lock admits one host per
   simulation. A plant another process holds is shown `running_elsewhere` and never
   written.
6. **Approval is human.** Only the user's **Approve plan** click (or `plant build` at
   the CLI) admits a plant, and a rebuild also needs the user's **I understand…**
   confirmation (`replaceExisting: true`). The model has no tool that builds, runs or
   removes one; a test asserts it. A stop or start it proposes happens only when the
   user confirms it in the page.
7. **No model key in the simulator.** The interview reaches a model only through Pulse
   Gage with the Pulse client's token. The Gage client sends requests only to
   `TIMEBASE_PULSE_URL` and refuses any other credential.
8. **No secrets in output.** Logs, errors, downloads, previews and the page carry no
   connection strings, secrets, tokens or raw exceptions.
9. **TLS verification stays on** for Pulse and Historian.

## Compatibility and contract freeze

Frozen at 2.0.0; a change needs a new version and the affected acceptance repeated:

| Contract | Value at 2.0.0 |
| --- | --- |
| Plant model | `schemaVersion` 1, `kind` `plant` |
| Engine output | `EngineVersion` 4 (snapshots from other versions are ignored and the plant replays from its start) |
| Storyline library | `kind` `storylines`, `schemaVersion` 1, optional `variants` |
| Postgres layout | `deployment/postgres/01_bootstrap.sql`, the simulator tables above, and each plant's table set |
| Web API | The endpoints in [plant-simulation.md](plant-simulation.md#the-web-container-phase-7) |
| Settings | `TIMEBASE_*` and `FLOWSIM_*` settings in `deployment/web/web.env.example` |
| Log events and codes | Every `web.*`, `interview.*`, `plant.*`, `hosting.*`, `gage.*`, `storyline.*` and `tool.*` code the simulator emits, with its meaning; codes are not renamed or reused |
| Timebase | Pulse and Historian 2.0.0 (see [compatibility.md](compatibility.md#release-200-rc1-november-22-2026)) |

## Operating limits

Plants stay small by owner decision: 2 lines, 6 units per line, 6 SKUs, 8 operators,
4 technicians, 6 customers, 20 parts, 12 failure modes, 6 preventive plans and 10 planted
incidents ([full rules](plant-simulation.md#limits-owner-decision-keep-plants-small)).
Backfill is up to the storyline's own history (14 days at most in the library); a
plant's own Dataset keeps 365 days. An interview costs about 60,000–75,000 input and
3,000 output Gage tokens with Claude Sonnet 4.6 and no prompt caching. No limit is set on
the number of plants one container hosts; acceptance ran three at once.

## Deferred beyond 2.0

- A login on the page, and multi-user access.
- Prompt caching and token streaming in the interview.
- A container registry push, image signing and SBOM publication.
- Storylines marked planned or later in the library.
- Suppliers and raw-material lots, utilities correlated with the process, multiple
  sites, and yield that changes discharged quantity.
- Moving BluebirdBev from its own container into the web container (owner's choice).

## Release authority

The owner approves 2.0.0. A bare `2.0.0` version must be allowed in
`scripts/package_release.py` with the owner's approval recorded there, as for 1.0.0. The
tag goes on the version-only commit whose CI run passes every job. Every gate in the
[acceptance matrix](release-2.0.0-acceptance.md) must be Passed, or accepted by the owner
as a recorded limit.
