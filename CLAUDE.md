# Start here — Claude Code handoff

Read `AGENTS.md` first. Its human-readable code, actionable errors, bounded safe
logging, test and documentation rules apply to this project. Then read
`docs/claude-code-handoff.md`, followed by the release specification and acceptance
matrix linked there.

v1.0.1 is released (tag `v1.0.1`, November 21, 2026; native linux-arm64 Docker; see
`docs/release-notes.md`). It is deployed in Docker for internal use. Any
change needs a new version number and the affected acceptance repeated.
Never open historical demo databases with the current runtime merely to inspect
them: opening can migrate state. Never automatically replay Uncertain work or
resume older backup Pending work on original tags. Do not print or commit secrets.

A transferred archive is a development handoff, not authorization to start hosts,
install services, change the Timebase stack, or publish historical values. Confirm
the current deployment and intended run with the user before new live acceptance.
Use the latest original database for normal restart. Disaster restore permanently
quarantines prior sessions and requires fresh tag names.

The handoff includes a suggested first-session prompt, architecture map, verified
commits, reproduction commands, known limits and a concrete next-work checklist.

Plant simulation (working name 2.0, November 21–22, 2026): see
`docs/plant-simulation.md` and `docs/design-plant-simulation.md`. The interview
skill is `skills/flow-data-simulator` (installed copy: `~/.claude/skills`).
Projects: `src/IndustrialDataSim.Core` (engine), `Runtime` (Postgres,
Historian, Pulse Gage client), `Interview` (the need-first interview tool
loop, no LLM dependency in Core), `Web` (`flow-data-simulator-web`: the
interview page with in-container plant hosting) and `Cli`. The web image is
built from `deployment/web/Dockerfile`; setup is `deployment/web/README.md`.

Running deployment: Postgres container `flow-data-sim-pg` (database
`flow_data_sim`) and plant container `bluebirdbev-plant` (image
`flow-data-simulator:2.0.0-dev`, built locally from source, not a release),
writing tags to the `Test` Dataset. The web container
`flow-data-simulator-web` (image `flow-data-simulator-web:2.0.0-dev`, local
build) can run beside it from the compose snippet; it shows BluebirdBev as
"running elsewhere" and never touches it. Live checks use simulation names
starting with `PlanTest`. A simulation only touches tables with its own
prefix; never touch another simulation's tables.

