<plan title="Phase 5: Release 2.0.0-rc.3">

<overview>
Phase 5 of the Plant browser plan (`plan.md`). Documents the Timebase look and the plant
browser, bumps the version to 2.0.0-rc.3, runs CI, tags, switches the web container to the
CI image and repeats the affected acceptance live.
</overview>

<context>
Assumes Phases 0–4 are committed on `web-timebase-look` and the web container is back on
`flow-data-simulator-web:2.0.0-rc.2`. The rc.2 release followed the same steps (CI run
45052990849; version-only commit "Allow the owner-approved 2.0.0-rc.2 candidate").
CLAUDE.md: any change needs a new version number and the affected acceptance repeated; confirm
the deployment and the live run with the user first.
</context>

<changes-required>

<change component="Engineering documentation">
**Files**:
- `docs/plant-simulation.md`: new section "Browsing a plant" (who is listed and why, the
  three sections, limits: 200 rows, 4 tags, 1,000 points, 31 days, 5-second queries,
  read-only transactions, schema-only identifiers), endpoint list under "The web container",
  error codes `browse.*`, `historian.read_too_large`, `historian.read_unexpected`.
- `deployment/web/README.md`: "Using the page" Plants paragraph rewritten for list + detail;
  troubleshooting rows for `browse.dataset_unknown`, `browse.historian_unavailable`,
  `browse.query_timeout`; note that plants created by other tools are not listed.
- `docs/design-plant-simulation.md`: round-5 bullet with the four owner decisions of
  November 22, 2026 (replace Plants; only this tool's environments; free range, 4 tags,
  many windows on one master range; one search box).
- `docs/release-2.0.0-specification.md`: browser scope, read-only and ownership rules, limits.
- `docs/release-2.0.0-acceptance.md`: gate **P27** "Plant browser on the candidate image"
  (list shows only page-created plants; Summary, table counts and a page of rows match
  Postgres; a trend matches a direct Historian read; Remove from the detail header).
- `docs/release-notes.md`: the "Unreleased" section becomes "2.0.0-rc.3: release candidate"
  covering the Timebase look and the browser, and that BluebirdBev-style plants no longer
  appear on the page.
- `skills/flow-data-simulator/SKILL.md`: already describes the rc.3 page (Review plan,
  Approve plan, attachments, rebuild, Gage kit) and the browser line still to add; copy it
  to `~/.claude/skills/flow-data-simulator/SKILL.md` only after the container runs rc.3.
- `CLAUDE.md`, `README.md`, `docs/claude-code-handoff.md`: rc.3 status and running deployment.
</change>

<change component="Local KB (never committed)">
**Files**: `../working-copy/docs/kb/90-data-simulator/what-is-the-flow-data-simulator.md`,
`log-events-and-error-codes.md` (and the copies in other worktrees that have `docs/kb/`)
**Changes**: Describe browsing plants; add the `browse.*` and `historian.read_*` codes; bump
`updated`. Check with `scratchpad/tools/kbcheck.py`. `git status` must not list `docs/kb/`.
</change>

<change component="Version">
**Files**: `deployment/web/Dockerfile` (`ARG VERSION=2.0.0-rc.3`),
`deployment/web/compose.snippet.yml` (`image: flow-data-simulator-web:2.0.0-rc.3`),
`scripts/package_release.py` (`DEFAULT_VERSION = '2.0.0-rc.3'`),
`.github/workflows/verify.yml` (`CANDIDATE_VERSION: 2.0.0-rc.3`)
**Changes**: Version-only commit "Allow the owner-approved 2.0.0-rc.3 candidate (version-only)",
made only after the owner approves cutting rc.3.
</change>

</changes-required>

<success-criteria>

<automated-verification>
- [x] `dotnet build -c Release` and `dotnet test -c Release` (with `FLOWSIM_TEST_PG`) pass.
- [ ] `git grep -n "2.0.0-rc.2"` shows only history (release notes, compatibility, acceptance records).
- [ ] After merging to `main` (with the owner's OK) and pushing: the CI run passes every job.
- [ ] Tag `v2.0.0-rc.3` on the version-only commit whose CI run passed; pushed.
</automated-verification>

<end-to-end-verification>
Confirm with the user before the live run (one interview, about 40k input tokens).
- [ ] Download the CI artifacts into `working-copy/.tools/v200rc3-ci-<sha>/`; check the arm64 web
      tar's SHA-256 against the run's checksum; `docker load`; image carries both tags.
- [ ] Recreate `flow-data-simulator-web` on `flow-data-simulator-web:2.0.0-rc.3` with the same
      env, certs and network; `/api/health` healthy; `Cache-Control: no-cache` on `/`; fonts served.
- [ ] `bluebirdbev-plant` untouched (same image, uptime continues); BluebirdBev not listed on the page.
- [ ] P27: approve a new `PlanTestRc3` plant from a short interview; Summary, table counts and one
      page of rows match Postgres; one trend matches a direct Historian read for the same tag/range;
      Remove from the detail header with "Also delete its Dataset" ticked; Dataset gone.
- [ ] P23–P26 on the same image: the page in light, dark and at phone width; attach a CSV,
      an image and a PDF in one interview; ask the interviewer to change `PlanTestRc3` and
      rebuild it (both confirmations; only its tables and its Dataset emptied, BluebirdBev
      untouched); download the three Gage kit files and confirm they hold no password.
- [ ] `compatibility.md` gets an rc.3 section (image IDs); acceptance P23–P27, P10 and P13 marked Passed with the date.
</end-to-end-verification>

</success-criteria>

<post-verification>
1. Cleanup (ask before deleting): dev images `flow-data-simulator-web:2.0.0-dev-browser`,
   `…:2.0.0-dev-dataset`; merged worktrees `wt-timebase-look`, `wt-plant-dataset`, `wt-docs-2.0`;
   the scratchpad preview proxy.
2. Update the memory file `data-sim-project-state.md` with the rc.3 state.
</post-verification>

</plan>
