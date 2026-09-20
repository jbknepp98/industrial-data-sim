# Current development handoff

This page describes current status and the next work. Historical shutdown notes,
repair milestones and test counts belong in the [implementation log](implementation-log.md)
and dated [audit](audit-2026-09-19.md), rather than serving as competing instructions.

## Implemented

- Deterministic finite generators, sequences, local Boolean triggers and pause/continue gates.
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

## Current limitation and next step

The [September 20 diagnosis](control-latency-diagnosis-2026-09-20.md) reproduced
multi-second control delays. The underlying runtime/OS cause remains unconfirmed.
The diagnostic-helper fixes preserve better evidence; they are not a latency fix.
Next, capture a slow occurrence with the repaired tooling and correlate launch,
client and host timing with any available native traces. Do not infer that an
uncaptured interval was fast or increase timeouts without evidence.

A fifteen-minute mixed-load test and a two-session live Test smoke passed; see
[the current verification report](phases-1-3-verification-2026-09-20.md).
Hours/days-long soaks, richer typed conditions, partitioned archives and resident
production live controls remain unfinished. The
[blind-publish policy](delivery-recovery.md) is approved: publish completion,
arrival indicators and user feedback are separate evidence. Per-point receipts
are not required. Real authentication, HTTP publishing and bounded arrival checks now run behind a
separate production state boundary; see [production v1](production-delivery-v1.md).

## Resuming work safely

1. Read [AGENTS.md](../AGENTS.md), inspect Git status and preserve newer user changes.
2. Review the current [runtime contract](durable-runtime-v1.md),
   [host contract](continuous-host.md), [logging](runtime-logging.md) and the
   diagnostic report before changing those components.
3. Work in small increments with useful errors, appropriate tests and matching docs.
   Follow the remaining [Phase 1 plan](phase-1-plan.md).
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
