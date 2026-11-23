# Start here — Claude Code handoff

Read `AGENTS.md` first. Its human-readable code, actionable errors, bounded safe
logging, test and documentation rules apply to this project. Then read
`docs/claude-code-handoff.md`: its first part covers the current 2.0 work (status,
reading order, architecture, rules for live checks, next work), and its second part
is the original 1.0 handoff.

The repository holds two products:

- **Plant simulator, 2.0.0-rc.3** (tag `v2.0.0-rc.3`, November 23, 2026; a candidate,
  not a release: rc.2 plus the Timebase Plant Simulator page, uploads, rebuild in
  place, the Gage kit and the plant browser). Scope and gates: `docs/release-2.0.0-specification.md` and
  `docs/release-2.0.0-acceptance.md`. How it works: `docs/plant-simulation.md`; owner
  decisions: `docs/design-plant-simulation.md`. Projects: `src/IndustrialDataSim.Core`
  (engine), `Runtime` (Postgres, Historian, Pulse Gage client), `Interview` (the
  need-first interview tool loop; Core has no LLM dependency), `Web`
  (`flow-data-simulator-web`: the interview page with in-container plant hosting) and
  `Cli`. The web image is built from `deployment/web/Dockerfile`; setup is
  `deployment/web/README.md`. The interview skill is `skills/flow-data-simulator`
  (installed copy: `~/.claude/skills`; keep it in sync).
- **Session simulator, 1.0.1** (tag `v1.0.1`, November 21, 2026; released, deployed in
  Docker for internal use). Unchanged by 2.0. Never open historical demo databases
  with the current runtime merely to inspect them: opening can migrate state. Never
  automatically replay Uncertain work or resume older backup Pending work on original
  tags. Use the latest original database for normal restart. Disaster restore
  permanently quarantines prior sessions and requires fresh tag names.

Any change needs a new version number and the affected acceptance repeated. Do not
print or commit secrets. A transferred archive or a handoff note is not authorization
to start hosts, install services, change the Timebase stack or publish historical
values: confirm the deployment and the intended run with the user before new live
acceptance.

Documentation is part of every change. Engineering documents live in `docs/`.
Customer-facing articles for the Timebase docs (docs.timebase.com) are kept on the
owner's machine in `docs/kb/`, which git ignores: never commit or push them. Follow the
rules in `docs/kb/README.md` there, and update an article when the behavior it describes
changes.

Running deployment: Postgres container `flow-data-sim-pg` (database `flow_data_sim`);
plant container `bluebirdbev-plant` (image `flow-data-simulator:2.0.0-dev`, a local
build, not a release) writing tags to the `Test` Dataset; and the web container
`flow-data-simulator-web` on 127.0.0.1:8480 from the compose snippet (image
`flow-data-simulator-web:2.0.0-rc.2` until the owner switches it to the CI-built
`2.0.0-rc.3`, which is loaded locally), hosting the page-built plant Gallo. From rc.3 the
page lists only plants it built, so BluebirdBev is not shown and never touched. A test
web container `flow-data-simulator-web-look` on 127.0.0.1:8481 runs rc.3 for previews. Live checks use simulation names starting with
`PlanTest`. A simulation only touches tables with its own prefix; never touch another
simulation's tables. Do not change Pulse settings without asking.
