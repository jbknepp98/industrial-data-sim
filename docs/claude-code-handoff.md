# Claude Code project handoff

Prepared October 21, 2026 (America/New_York). Owner: Jeff Knepper. Collaborators
mentioned in prior demonstrations: Vaughan and Lenny. Project name:
**timebase-historian-data-sim**. GitHub repository:
https://github.com/jbknepp98/industrial-data-sim.git

This document transfers engineering context, not credentials. Do not copy secrets
from earlier chat transcripts into code, prompts, logs or public documentation.
The transfer's top-level inventory describes the exact files included/excluded.

## Suggested first message to Claude Code

> Read CLAUDE.md, AGENTS.md and docs/claude-code-handoff.md. Inspect the repository
> status and release acceptance matrix before changing anything. We have finished
> the Phase 4 candidate packaging work, but have not completed live release
> acceptance or released v1.0.0. Summarize current state and propose the exact
> Phase 5 acceptance run, preserving all ordering, recovery and credential rules.
> Do not launch old demos, migrate their databases, install services or publish
> until we agree on the current deployment and test run. Work incrementally;
> test your changes, keep errors useful, and update documentation as you go.

## Status at transfer

Baseline source HEAD before handoff documents: `60e9409` on `main`, pushed to
origin. It records Phase 4 qualification. The archive includes the current working
files and a separate Git bundle; consult its manifest for the actual handoff HEAD.
Do not confuse the source tree's commit, candidate product version, model schema,
generator identity, database schema, or archive format.

- Implemented/qualified release phases: 1 scope; 2 bounded execution/capacity;
  3 recovery/retention; 4 self-contained packaging and compatibility baseline.
- Released version: **1.0.1** (tag `v1.0.1` on `192aaab`): the 1.0.0 program plus a
  native `linux-arm64` package. The owner deploys only in Docker. `v1.0.0` (`cdf0681`)
  was a version-only rebuild of the accepted `1.0.0-rc.2`; rc.1 is incompatible
  with Historian 2.0.0.
- SQLite schema: **10**. Archive export format: **3**. Evidence-only snapshot
  schema marker: **1000008**, deliberately not runnable.
- Last fully qualified implementation: `afb669d`.
  [All six CI jobs](https://github.com/jbknepp98/industrial-data-sim/actions/runs/39325253000)
  passed native package and regression checks on Linux x64, macOS ARM64 and
  Windows x64: **872 .NET tests and 36 Python tests per OS** plus independent
  pattern/schema/host/capacity checks.
- `60e9409` is the documentation follow-up to that implementation. The above
  counts are recorded evidence, not a claim that tests were rerun on October 21.
- The exact clean CI macOS package was downloaded and tested on the local Mac.
  It bundles .NET 10.0.13; an earlier local package bundled 10.0.12. Inspect each
  package manifest/runtimeconfig rather than assuming every build is identical.
- Phase 3 implementation `5818203` passed three-platform CI:
  https://github.com/jbknepp98/industrial-data-sim/actions/runs/39316708960
- No service was installed and no Historian samples were written during Phase 4.
  Transfer preparation likewise does not start or change the deployment.

## Product requirements and the owner's working style

This is an agent-authored industrial data simulator, not a UI/model-editor product.
Models are declarative JSON; no human-written executable simulation scripts are
required. Human readability nevertheless matters: understandable names, focused
methods, comments explaining invariants and recovery choices, not clever code.
Jeff expects slow, independent, checked increments and thorough documentation.
Errors must explain what failed, identify a stable code and structured location,
and give useful troubleshooting/correction steps without leaking private data.
Read AGENTS.md; it is the authoritative development-process document.

Multiple simultaneous sessions are required. Each session targets exactly one
Dataset. Sessions may share a Dataset only with disjoint output tags. Ownership
is enforced within one state database; it is not a distributed reservation system.
Different databases and outside writers must be assigned disjoint tags operationally.
Avoid Dataset special characters other than hyphen, underscore and space; use the
implemented validator as the exact accepted naming contract.

Capabilities include constants, bounded ramps, staircases, randomized holds,
sequences with randomized durations, optional noise/faults, Boolean gates and
conditional manufacturing models. Numeric, Boolean and string tags are supported.
A string SKU routes production to exactly one of two downstream packaging cells.
Groups may depend on other signals/states; readiness can delay a sequence.
A gate closing suppresses output and either pauses the pattern clock or lets it
continue without buffering/sending its suppressed samples. Opening the gate must
follow the configured time policy, not silently restart the pattern.

Totalizers can integrate elapsed-time rates OR randomized production increments
until a production target is reached. A simulated production batch is different
from an HTTP transport batch. Do not reintroduce the earlier incorrect blanket
rule that all accumulation must depend only on elapsed time.

## Nonnegotiable delivery and recovery contracts

1. Emit strictly increasing timestamps per tag, including across batches/restarts.
   Even if a server accepts older values in some circumstances, this simulator
   must not rely on historical insert, replacement or silent timestamp shifting.
2. Historian ignores duplicate timestamps per tag: first value wins, even if a
   later value or quality differs. Do not infer safe retries from this behavior.
3. Publishing is blind. HTTP completion is **Published**, not a per-point receipt.
   Current-value arrival/change observations are indicators; human pattern review
   is separate evidence. Counts, missing repeated samples and read-back gaps do
   not prove failed delivery and never authorize a replay.
4. Persist exact pending payload and generation checkpoint transactionally. Do
   not reroll randomness, reset clocks/totalizers or repeat production events.
5. Interrupted Sending becomes Uncertain on ordinary startup. Preserve payloads,
   ownership and evidence. No automatic POST replay, force-ack or reinterpretation
   of uncertainty as Pending. Read-only auth refresh is separately bounded.
6. Normal restart uses the newest original database and keeps tag/process identity.
   Older backups may omit later successful sends even if they show Pending.
   Guarded disaster restore permanently quarantines all restored sessions;
   fresh work requires its generated `Recovered.<uuid>.` tag prefix. Same-tag
   disaster continuation is outside v1. This is the conservative engineering
   default documented during Phase 3, not an assertion of a new server guarantee.
7. Completed-history segments may be archived in bounded portions while preserving
   model state, progress and tag ownership. Unresolved work cannot be pruned to
   meet a budget. Audit-history rollover is not a new session or a reset totalizer.
8. TLS hostname/chain validation stays enabled. Keep credentials out of model
   JSON, SQLite, CLI arguments, operational logs and public Git history.

## Architecture and where to look

| Area | Starting points | Responsibility |
| --- | --- | --- |
| Configuration/model validation | src/IndustrialDataSim.Core/Configuration and Validation; schemas; examples | Typed, bounded input contracts and diagnostics |
| Pattern generation | src/IndustrialDataSim.Core/Simulation | Deterministic TVQ streams and windows |
| Manufacturing/process logic | Core manufacturing files; docs/manufacturing-process.md | Conditions, dependencies, states, noise/faults, accumulation, windowed checkpoints |
| Durable state | src/IndustrialDataSim.Runtime/DurableRuntime*.cs; Schema.cs | Lifecycle, ownership, queues, transactions, cursors, migration |
| Production transport | HistorianClient.cs; HistorianConnection.cs; ProductionDelivery.cs | OAuth/TLS, preflight, publish, observations, no unsafe retries |
| Scheduling | ProductionWorker.cs; ProductionFollower.cs; CLI ContinuousSimulationHost.cs | Fair bounded turns, backfill-to-live pacing, local controls |
| Recovery and archival | StateBackup*.cs; DurableRuntime.Quarantine.cs; DurableRuntime.Archive*.cs; ArchiveStorage.cs | Guarded evidence, fresh-tag recovery, durable export/pruning |
| CLI | src/IndustrialDataSim.Cli | Validation, session/fake host, production commands, bounded JSON/IPC |
| Tests | tests/IndustrialDataSim.Tests; tests/IndustrialDataSim.CrashProbe | Unit/integration contracts and actual killed-process drills |
| Capacity/preview tools | tools/IndustrialDataSim.CapacityProbe; tools/IndustrialDataSim.DemoPreview | Synthetic throughput/history and independent previews |
| Verification/operations | scripts | Python oracles, live test helpers, supervisor, package assembly/checks |
| Deployment | deployment; docs/installation.md | Reviewed templates; no automatic service installation |

Simulation-only commands (`session`, `host`, `live`) use fake transport. Production
commands (`production`, `production-host`, `production-live`) can authenticate and
write real data. Help/argument validation is not permission to run an arbitrary
production mutation. A host owns its state; controls use same-user local IPC.
Use the same account and exact canonical database path. A stop reply accepts the
request; wait for process exit before reopening the database directly.

## Read documentation in this order

1. AGENTS.md and this handoff.
2. docs/release-1.0.0-specification.md and release-1.0.0-acceptance.md: current scope
   and explicit release gates. These supersede the original implementation plan's
   sequencing. Older docs named "phase 1" may refer to the original project phase.
3. docs/phase-4-qualification.md, installation.md, compatibility.md,
   phase-4-security.md: latest package evidence and remaining limits.
4. docs/state-backup-recovery.md, delivery-recovery.md, audit-archival.md:
   restart versus disaster restore, blind publishing and retention.
5. docs/phase-2-qualification.md and phase-3-qualification.md: measured workload,
   CPU/storage assumptions, crash/upgrade drills and history bounds.
6. docs/verification.md and resume-development.md: reproduction and chronology.
   Treat dated historical entries as history, not current deployment status.
7. docs/timebase-api-findings.md, production-delivery-v1.md, production-host.md,
   continuous-host.md, runtime-logging.md, and the pattern-specific contracts.

## Build and test from a fresh checkout

Requires .NET 10 SDK (global.json permits latest stable feature band), Python 3.12
for the documented tooling, and network/package access on initial restore.

```sh
dotnet restore IndustrialDataSim.slnx
dotnet build IndustrialDataSim.slnx --configuration Release --no-restore --disable-build-servers
dotnet test IndustrialDataSim.slnx --configuration Release --no-build --no-restore
python3 -m venv .tools/schema-check
.tools/schema-check/bin/python -m pip install -r scripts/requirements.txt
.tools/schema-check/bin/python -m unittest discover -s scripts -p 'test_*.py'
.tools/schema-check/bin/python scripts/verify_offline.py
.tools/schema-check/bin/python -O scripts/verify_host.py
```

On Windows use the venv's Scripts/python.exe path. Do not assume copied venvs,
obj/bin outputs or the bundled local SDK are portable to another OS/CPU. Restore
and rebuild there. No Node frontend/build is involved.

The Mac's local `.tools/dotnet/dotnet` is SDK 10.0.401. The normal launcher stalled
once during Phase 3; root cause was not established. A direct MSBuild invocation
worked with DOTNET_ROOT, DOTNET_HOST_PATH and DOTNET_CLI_HOME pointing to the local
SDK/config, NUGET_PACKAGES to the local cache, and TMPDIR=/tmp:

```text
.tools/dotnet/dotnet .tools/dotnet/sdk/10.0.401/MSBuild.dll IndustrialDataSim.slnx /t:Build,VSTest /p:Configuration=Release /p:UseSharedCompilation=false /m:1 /nr:false /v:minimal
```

This is a documented fallback, not a reason to change production code or suppress
failures. Clean CI used ordinary build/test commands successfully.

For packaging, use a new ignored output directory each time:

```sh
python3 scripts/package_release.py --rid osx-arm64 --output .tools/new-candidate
python3 scripts/verify_package.py .tools/new-candidate/industrial-data-sim-1.0.0-rc.2-osx-arm64.zip --require-clean
python3 scripts/verify_deployment_templates.py .tools/new-candidate/industrial-data-sim-1.0.0-rc.2-osx-arm64
```

Other qualified RIDs: linux-x64, win-x64. Packages include .NET; direct execution
needs neither SDK nor Python. Optional Python supervisor requires Python and only
its standard library. CI manifests identify exact source commit/dirty state and
runtime versions; a dirty developer package is not release evidence. ZIPs are not
code-signed/notarized. GitHub artifacts have 14-day retention; preserve exact
candidate ZIP/checksum/audit evidence locally before that expires.

## Current stack and private configuration

The user supplied a Docker Desktop screenshot October 19. Read-only Docker
inspection confirmed Historian and Pulse `2.0-gc1`, both Linux ARM64; exact image
digests are in docs/compatibility.md. A verified-TLS Python probe authenticated and
confirmed Test Dataset API shape with seven-day purge-age retention. It wrote no
samples. These observations are dated evidence, not a claim the stack is unchanged
on the day Claude Code first runs.

Local endpoint history: Historian https://localhost:4512, Pulse typically
https://localhost:4542. Read the private environment on the actual machine; do
not blindly hardcode these origins or assume Docker-internal names resolve from
another machine. A certificate must match the name used by the client.

`.env.example` lists setting names, not values. `.env` and certs are ignored.
Secrets were rotated during stack upgrades; do not reuse values from chat history.
If the transfer omits them, obtain current credentials and trusted root certificates
separately. Read-only probing: `scripts/probe_compatibility.py --environment-file
<private-file>`. Never print the private file to an agent transcript.

Do not change retention just to make a test pass. Record backfill evidence promptly
because seven-day retention can remove early history during the planned live soak.

## Local-only evidence and old demos

The transfer inventory distinguishes source, downloaded candidate artifacts,
local build/tool caches, runtime files and excluded sensitive material. Local
runtime files are preserved evidence, not an instruction to launch them. The
archive is not a guarded StateBackup snapshot and does not confer safe replay.

Notable local paths (if present in the transfer):

- `.tools/phase4-ci-macos/`: exact `afb669d` macOS ZIP, receipt and audit JSON.
- `.tools/phase3-final-tests.log`, phase3-history-load.json,
  phase3-legacy-upgrade.json: local 872-test, 40,000-batch retention and real old-CLI
  schema-8-to-10 upgrade/rejection evidence. Sanitized summaries are versioned.
- `.tools/phase3-legacy/`: old CLI drill build, not the current product.
- `.tools/phase4-package-local*`: intermediate dirty developer packages, including
  incomplete/failed attempts. Do not promote them over the clean CI artifact.
- `.tools/packaging-demo-20261002T1832-a16929/` and its speeds subdirectory:
  historical demo databases/launch material, originally schema 6 with old binaries
  and a finite horizon that expired October 9. No current running-state assertion
  is made. Never open them with schema-10 runtime for casual inspection.
- Other dated `.tools/*smoke*`, manufacturing qualification folders, previews,
  logs and audit exports: historical investigations, not current approval to write.

Transfer preparation found no matching simulator/supervisor processes through a
read-only process inspection. That is a point-in-time check, not a database
consistency guarantee or server delivery receipt. Preserve WAL/SHM companions
with databases. Do not merge transferred state with another copy that has advanced.

## Next work: Phase 5, then Phase 6

Status, November 21, 2026: all steps are done. **v1.0.1 is released** (tag on
`192aaab`; v1.0.0 on `cdf0681`; see release-notes.md). It is deployed in Docker for the owner's
team; see the Docker section of installation.md.

1. Confirm current stack identity, credentials/TLS trust, Dataset retention,
   available disk and the chosen deployment machine/account/startup owner.
   Inspect the acceptance matrix; do not restart an old demo as the RC run.
2. Select/freeze an exact candidate and preserve its source commit, ZIP/hash,
   dependency audit, manifest, model JSON/seeds and host configuration. Prepare
   four concurrent sessions / 32 tags; at least two share a Dataset using disjoint
   fresh tags. Include simple signals, gated sequences, two-cell SKU routing and
   a target-bounded production model. Match the release workload specification.
3. Review previews and independently check pattern/order/route/interlock behavior.
   Prepare 72-hour historical ranges and a continuing live horizon using windowed
   manufacturing execution; do not just raise precompiled-model limits.
4. With the user, authorize the exact live run and test Dataset/fresh namespace.
   Backfill and transition seamlessly into three hours of real time (owner scope
   decision October 26, 2026; originally seven days) without
   re-admission, reset clocks or new random seeds. Target-bounded models may finish.
5. Record arrivals/changes separately from Published progress. Measure queue,
   memory/disk, per-session fairness, backlog drain, control latency and published
   timestamp lag (healthy p95 targets: 5 seconds control, 10 seconds live lag).
   Keep healthy outliers in the data; label fault/maintenance windows separately.
6. Exercise actual deployment startup ownership, graceful restart, extension and
   safe history maintenance. Do not replay Uncertain writes or reinterpret missing
   repeated samples as failed delivery. Preserve exact state and review failures.
7. Obtain human pattern review. Elapsed wall time, point counts and a running host
   do not establish that the intended manufacturing behavior is correct.
8. Phase 6: final code/docs/security audit, exact-candidate CI/artifact verification,
   release notes/limitations, owner approval, then immutable v1.0.0 tag/artifacts.
   A behavior-changing repair invalidates affected soak evidence; rerun it.

## Limits and decisions still open

- v1.0.0 released November 21, 2026 with owner approval. The three-hour live soak
  (reduced from seven days, October 26) passed with rc.2. Future changes need a
  new version and the affected acceptance repeated.
- OS startup templates were parsed and package lifecycles tested, but no actual
  service registration/boot test was performed on the user's deployment machine.
- No project distribution license selected by owner; public GitHub visibility is
  not a downstream license grant. Third-party notices are bundled separately.
- Private-CA trust currently validates hostname/chain/validity/server auth but does
  not check revocation. Document operational trust rotation; do not claim more.
- Signing/notarization and organizational trust expectations remain release review.
- No same-tag disaster restore, distributed ownership, cross-session live input
  subscriptions, arbitrary executable model code or unrestricted model nesting.
- Finite horizons and guarded extensions; 1,000 revision limit, 100 unarchived
  sessions, 32 production tags/session, 128 MiB archive export limit. Safety
  ceilings are not general capacity guarantees. Reference planning allowance is
  10 GiB available storage, increased for archives/backups/larger workloads.
- No credentials/private configuration should be committed or included in a public
  handoff. Review the transfer manifest's exclusions before declaring migration
  complete; obtain omitted secrets through an appropriate private channel.
