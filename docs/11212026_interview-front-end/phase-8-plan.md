<plan title="Phase 8: Packaging, docs and live end-to-end">

<overview>
Phase 8 of "Interview Front End for the Flow Data Simulator". Ships the web
container as something a Timebase user can install, then proves the whole flow
live with two audiences:

- a beverage plant for maintenance and operations;
- a chemicals batch plant for a process engineer and quality.
</overview>

<context>
Assumes phases 2, 4 and 7 are complete: every storyline marked available
works, and the page, approval and hosting all work.
</context>

<changes-required>

<change component="Image and compose">
**Files**: `deployment/web/Dockerfile`, `deployment/web/compose.snippet.yml`, `deployment/web/README.md`

- **Image:** a self-contained `linux-x64` and `linux-arm64` publish of
  `IndustrialDataSim.Web`, on the same base as the CLI image.
- **Runtime settings:** a non-root user, stop signal SIGINT, and a health check
  on `/api/health`. The storyline library and base models are copied into the
  image.
- **Compose snippet:** joins the Timebase network, maps one port, and passes an
  env file (Pulse URL and client, CA bundle, Dataset, `FLOWSIM_PG_CONNECTION`).
  It also contains `flow-data-sim-pg` for installs that don't have it yet.
- **Setup steps (README):**
  1. Create the Pulse client `DataSim` in Pulse → Clients.
  2. Give it a Soft client policy in Pulse → Gage.
  3. Choose the Gage model handle.
  4. Check that the Dataset's purge age covers the backfill.
  5. Run `plant gage-check`.
</change>

<change component="CI">
**File**: `.github/workflows/*`

- Build and test the Web and Interview projects in the existing jobs.
- Add the web image publish for the 4 package RIDs, or document why only the
  Linux ones apply, since the web image is Docker-only.
- The dependency audit covers ASP.NET Core and `Anthropic`.
</change>

<change component="Docs and skill">
- **`docs/plant-simulation.md`:** a web front end section covering the
  certification and batch areas, `gage-check`, and hosting inside the
  container.
- **`docs/design-plant-simulation.md`:** round-4 decisions (front end, Gage
  reuse, explain and stop, in-container hosting, golden batch scope).
- **`skills/flow-data-simulator/SKILL.md`:** point users to the web page when
  it's installed, and keep the CLI path. Sync the installed copy.
- **`CLAUDE.md`:** list the new projects and the running deployment.
</change>

</changes-required>

<success-criteria>
<automated-verification>
- [ ] CI is green on all jobs (offline builds, packages, dependency audit) — pending: runs when the parent session merges and pushes (web-image job and web audit added; full suite 1,129/1,129 locally with FLOWSIM_TEST_PG)
- [x] `docker build` of the web image succeeds for linux-arm64 locally; the image runs as non-root (`docker inspect`) — `flow-data-simulator-web:2.0.0-dev`, User datasim (uid 10001), StopSignal SIGINT, health check
</automated-verification>
<end-to-end-verification>
- [x] Fresh install drill: following only `deployment/web/README.md`, start the web container in the live stack (Pulse client created with the owner's approval). `plant gage-check` passes (owner chose to reuse the existing `DataSim` client)
- [x] Audience A: "beverage bottler; plant manager, maintenance lead and quality manager; late orders, repeat failures and quality holds; SAP and Ignition". The interview proposes storylines 1, 2 and 5b (2 lines: syrup tank plus filler); approve; plant live; verify script 0 mismatches; every demo-guide step performed in Explorer/Connect works — after adding storyline variants (1 and 2 on `beverage-syrup-batch`), live PlanTestBevA: 0 mismatches on 125,220 tag points and 712 rows; Explorer/Connect steps work; Gage steps not driven
- [x] Audience B: "specialty chemicals; process engineer and quality manager; golden batch and holds; safety compliance". The interview proposes storylines 5a, 3a and 3b; approve; batch plant live; drifted batches and the uncertified closure visible as the demo guide says — after variants (3a and 3b on `chemicals-batch`) and the backfill guard, live PlanTestChemB2 (14 days): 0 mismatches on 116,334 tag points and 716 rows
- [x] Restart the web container: both plants resume from snapshots; the page shows both live
- [x] Remove both test plants from the page; `flow_data_sim` contains only the other simulations' tables; nothing written outside the Test Dataset
- [x] Record token usage per interview (Pulse `/api/gage/usage`) in the docs as the expected cost — from the interview store (`flowsim_interview_messages`); Pulse's usage page needs an administrator
</end-to-end-verification>
</success-criteria>

<post-verification>
Commit and push. Report to the owner with:
- the verification evidence;
- token cost per interview;
- known limits (no login on the page; the storylines still marked later);
- a recommendation on versioning (2.0.0-rc.1) and acceptance before any release tag.
</post-verification>

</plan>
