# Restart after the planned shutdown

Development resumed after the shutdown checkpoint; logging and audit R1–R5
repairs and R6 are complete, with the listed documentation drift corrected.
No production Historian writer or resident simulator service has been started
by these increments. No Historian writes were performed. There is no requirement
to leave a terminal or development process running to preserve this work.

## Completed and verified

- Optional operational logging: readable explanations/actions, bounded rotating
  files, safe context, and isolation of logger failures from durable operations.
- R1: explicit RetryGeneration after an oversized-point failure, with checked
  limits, immutable configuration, retained ownership, and no unresolved writes.
- R2: corrected the example driver to account for delivery progress.
- R3: schema version 2 indexes only outstanding batches for queue accounting.
  Existing version 1 databases upgrade transactionally.
- R4: configuration-integrity failures stop the affected session while eligible
  peers continue; storage failures still propagate.
- R5: generation errors identify tag index, candidate slot, and sample time.
- R6: independent session progress survives release/reuse; schema version 3
  reconstructs old reports transactionally from retained model/batch metadata.
- Latest verification: 520 Release .NET tests passed in a clean source export.
  Details are recorded in [the September 19 audit](audit-2026-09-19.md)
  and the implementation log, including continuous-host process checks. Repository-visible secret-marker/local-link and whitespace
  checks passed after the latest increment.

These increments use a sealed fake Historian. The owner has selected blind publishing with arrival indicators and user review;
see docs/delivery-recovery.md. Production delivery is not yet implemented. Local configuration,
certificates, state databases, tooling, and logs remain ignored by Git; they are
not part of the source checkpoint. Preserve local storage through shutdown.

## Resume here

The next verification checkpoint adds a cross-platform CI workflow and a
[reproducible capacity baseline/retention design](capacity-and-retention.md).
Local build, 520 .NET tests, eight Python tests, schema/oracle checks, host smoke,
and twelve capacity runs passed. Check the GitHub workflow result before claiming
Linux/Windows verification. Archive/deletion and sustained capacity testing remain
future work; the production adapter follows the approved blind-publish policy.

1. Read AGENTS.md, docs/audit-runtime-2026-09-18.md and its follow-up sections,
   docs/durable-runtime-v1.md, and docs/runtime-logging.md.
2. Inspect Git status before editing; preserve any newer user changes.
3. Review the completed audit repairs and inspect uncommitted work before any
   commit/push; R4, R6, cancellation, and the session CLI were completed after the shutdown checkpoint.
4. Runtime cancellation, lifecycle CLI, and bounded foreground worker are implemented
   (docs/session-cancellation.md, docs/session-cli.md, docs/simulation-worker.md).
   The [continuous host](continuous-host.md) now supports same-user local live
   controls. Review [the September 19 audit](audit-2026-09-19.md) before the next
   feature increment. Next model work should define general typed conditions and
   richer sequence behavior in small tested increments. The owner-approved blind-
   publish contract now permits production adapter/arrival-monitor implementation;
   preserve a separate production state boundary and no automatic replay.
5. Preserve readability, actionable errors, logging, and incremental verification.
6. Continue docs/phase-1-plan.md: session controls/worker, remaining conditions and
   patterns, blind-publish adapter and arrival monitoring, acceptance and capacity
   testing, and final code/documentation review. Work in small tested increments.

Do not replay Uncertain batches or force acknowledgement. Recovery uses SQLite,
not logs or missing retained samples. No automatic job restart is configured by
this project. Existing credentials should remain local and must not be repeated
in public-facing documents, logs, or commits.

## Verification command

With the existing project-local SDK/cache available:

```sh
DOTNET_CLI_HOME="$PWD/.tools/cli-home" NUGET_PACKAGES="$PWD/.tools/nuget" \
  .tools/dotnet/dotnet test IndustrialDataSim.slnx --configuration Release \
  --no-restore --disable-build-servers
```

If caches are absent, restore dependencies first with a .NET 10 SDK. No live
Historian is needed for these tests. The new R1 recovery boundary tests use
injected exceptions and reopen; existing process recovery tests kill child
processes at durable boundaries.
