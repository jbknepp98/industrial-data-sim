# Current development handoff

This page describes current status and the next work. Historical shutdown notes,
repair milestones and test counts belong in the [implementation log](implementation-log.md)
and dated [audit](audit-2026-09-19.md), rather than serving as competing instructions.

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

## October 1 checkpoint

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
finite end and restart procedure. Inspect that process before starting another publisher. Continue the Windows recovery
investigation and the next simulation-feature increment after this reliability
checkpoint; do not label the previous demonstration accepted.

## Other current limitations

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

The [packaging demonstration](packaging-demo-2026-09-20.md) records its live process,
review tags and explicit restart instructions. Inspect that run before starting another publisher.

The earlier docs-only `f083814` CI run also exposed Windows owner-file acquisition
and cleanup failures in crash-recovery tests. They were discovered during the
packaging review and remain unconfirmed; see the demonstration report. A subsequent
green run is not proof that this intermittent Windows issue is resolved.
