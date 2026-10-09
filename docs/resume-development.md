# Current development handoff

This page describes current status and the next work. Historical shutdown notes,
repair milestones and test counts belong in the [implementation log](implementation-log.md)
and dated [audit](audit-2026-09-19.md), rather than serving as competing instructions.

## Current release work — October 9

The five October 7 audit findings were repaired in `ea29007`; Windows, Linux and
macOS CI passed. See the [audit repair record](audit-2026-10-07.md).
Release Phase 1 defines the [1.0.0 specification](release-1.0.0-specification.md)
and [acceptance gates](release-1.0.0-acceptance.md). Release Phase 2 is now qualified
on the reference workload; see [the results and limits](phase-2-qualification.md).
Opt-in windowed manufacturing removes full-horizon storage/reconstruction costs,
with schema-eight atomic process checkpoints. Existing models keep their original
semantics. The production host serves controls between complete session turns.
The next phase is operational recovery, backup/upgrade, storage and rollover.
Do not open older live databases with the new binary without a planned backup
and upgrade: opening migrates their schema. No existing demo was migrated here.
The dated demo observations below are historical, not a fresh process-health check.

## Implemented

- Deterministic finite generators, sequences, local Boolean triggers and pause/continue gates.
- String SKU timelines and exclusive routes from shared SKU/readiness sources.
- Wall-clock paced production follow: the same durable session catches up and continues live.
- SQLite checkpoints, bounded queues, disjoint tag ownership, cancellation,
  recovery and separate per-session progress. Uncertain work is never replayed.
- A fake Historian, bounded worker, continuous foreground host and local live controls.
- Bounded operational logging with actionable errors and slow-host observations.
- Linux/macOS/Windows CI, reproducible capacity and history-growth measurements,
  a read-only control timing probe and bounded macOS native profiling.
- Explicit verified audit archival retains identity and timestamp protections.
- Separate production mode supplies Pulse authentication, verified TLS, ordered blind
  publishing, arrival observations and explicit user-review recording.
- Diagnostic reports now retain selected evidence on failure. Process-launch
  time is measured separately; missed launch capture opportunities are explicit.

The latest verified code checkpoints and exact test counts are recorded in the
[verification guide](verification.md). The older clean-source-export run is
historical evidence, not a claim that every subsequent increment was re-exported.

## October 1–2 checkpoint

The packaging demo stopped on September 21 with a read HTTP 401, seven unsent
samples and Failed state; it did not finish its scheduled seven-day run. Its
expired model and retained queue have not been restarted. See the updated
[demonstration report](packaging-demo-2026-09-20.md).

The client now checks authentication before each GET, refreshes/retries once on a
GET 401 within the original deadline, and measures expiry from token-request
start. Persistent failures preserve unsent work; no write is automatically retried.
The original 401's cause remains unconfirmed. Following the local stack rebuild,
the public Pulse `/api/ca` endpoint supplied the new CA. Project-local trust now
verifies both Pulse and Historian TLS; no system trust settings were changed.
The replacement credential resolved authentication on October 2. Created a fresh
Test dataset (seven-day purge age, no size purge, 100 ms late-data tolerance,
30-day late-data age) and ran `scripts/verify_production.py` with fresh state.
Two sessions completed two published batches: all six tags matched observations
and had non-null current values, and both ramp tags showed changes. User pattern
review remains NotReviewed; arrival evidence is not a per-point receipt.
See the October 2 entry in [the implementation log](implementation-log.md).
Preserve the old demo database as evidence; do not replay it into the rebuilt
stack. The new API describes late-data support, but the simulator retains its
forward-only timestamp policy. A fresh packaging run is now active: see the October 2
[backfill-to-live demonstration](lenny-demo-2026-10-02.md) for its tag prefix,
finite end and restart procedure. Both the original and additive-speed publishers
passed a controlled graceful stop/resume test on October 2 and remain active;
inspect both process records before starting another publisher. The original
batch records and model hashes were unchanged, and each resumed at its saved
candidate cursor. The October 6 read-only checkpoint found both databases still
publishing current values after roughly four days. Their running schema-six state
has not been migrated by the new runtime. Back up and plan an operational upgrade
before restarting those databases on the new build. Their finite end remains
October 9 at 18:32 UTC.

## October 6–7 implementation checkpoint

The three accepted increments are implemented:

- Public simulation/production horizon commands, idempotent audited revisions,
  schema-seven state, revision-aware archives and forced-crash checks.
- Resident production hosting with same-user live controls, health reporting,
  explicit startup configuration and bounded optional process restart.
- Bounded manufacturing process models: typed conditions, delayed feedback,
  repeating schedules, state machines, noise/faults/quality, elapsed-time and
  production-batch totalizers, and explicit target/condition termination.

See [the acceptance report](three-phase-acceptance-2026-10-07.md),
[production host](production-host.md) and [manufacturing contract](manufacturing-process.md)
for reproducible commands, verification and deliberate limits. Manufacturing
models precompile a bounded discrete clock; this is not an unlimited event engine.
There is no cross-session live dependency subscription. OS boot services are not
installed automatically. The acceptance host uses separate fresh Test tags/state.

## Remaining operational limits

The September control-latency diagnosis remains unresolved. Fast October control
measurements do not prove a general latency fix. Keep phase timing diagnostics
and investigate a captured slow occurrence before changing deadlines. Partitioned
archives remain future work. Process capacity is now measured for the explicit
reference workload; larger loads and lower-spec hardware are not certified.
Arrival observations and user feedback remain separate from blind publish completion;
counts and missing repeated samples never authorize replay. No old September
failed demonstration was restarted or labeled accepted.

## Resuming work safely

1. Read [AGENTS.md](../AGENTS.md), inspect Git status and preserve newer user changes.
2. Review the current [runtime contract](durable-runtime-v1.md),
   [host contract](continuous-host.md), [logging](runtime-logging.md) and the
   diagnostic report before changing those components.
3. Work in small increments with useful errors, appropriate tests and matching docs.
   Follow the [1.0.0 release gates](release-1.0.0-acceptance.md); the original
   [Phase 1 plan](phase-1-plan.md) remains historical implementation context.
4. Preserve local configuration and state through shutdown. Do not delete owner
   files, reset Uncertain batches or infer recovery authority from logs.

Offline development and CI use fake or scripted transports. Explicit production
commands perform real writes; no production service or automatic job restart is configured. Credentials, certificates, state,
profiles and logs remain local and ignored; never include them in public commits.
Stop a running host gracefully before shutdown. Restart it explicitly against
its existing database; do not re-admit existing sessions.

## Verification

With the project-local SDK/cache already restored:

```sh
DOTNET_CLI_HOME="$PWD/.tools/cli-home" NUGET_PACKAGES="$PWD/.tools/nuget" \
  .tools/dotnet/dotnet build IndustrialDataSim.slnx --configuration Release \
  --no-restore --disable-build-servers
DOTNET_CLI_HOME="$PWD/.tools/cli-home" NUGET_PACKAGES="$PWD/.tools/nuget" \
  .tools/dotnet/dotnet test IndustrialDataSim.slnx --configuration Release \
  --no-build --no-restore
python3 -m unittest discover -s scripts -p 'test_*.py'
```

Restore dependencies first if caches are absent. Building the entire solution is
necessary for standalone diagnostic tools. See the verification guide for schema,
host-process and capacity checks; no Historian connection is required.

The [packaging demonstration](packaging-demo-2026-09-20.md) records its live process,
review tags and explicit restart instructions. Inspect that run before starting another publisher.

The earlier docs-only `f083814` CI run also exposed Windows owner-file acquisition
and cleanup failures in crash-recovery tests. They were discovered during the
packaging review and remain unconfirmed; see the demonstration report. A subsequent
green run is not proof that this intermittent Windows issue is resolved.
