# Restart after the planned shutdown

Development is paused after the logging, audit R1, and audit R3 increments.
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
- R5: generation errors identify tag index, candidate slot, and sample time.
- Latest verification: 434 Release .NET tests passed. Prior offline/schema/gate
  checks passed. Repository-visible secret-marker/local-link and whitespace
  checks passed after the latest increment.

These increments use a sealed fake Historian. Production acceptance semantics
remain unresolved and production delivery is not enabled. Local configuration,
certificates, state databases, tooling, and logs remain ignored by Git; they are
not part of the source checkpoint. Preserve local storage through shutdown.

## Resume here

1. Read AGENTS.md, docs/audit-runtime-2026-09-18.md and its follow-up sections,
   docs/durable-runtime-v1.md, and docs/runtime-logging.md.
2. Inspect Git status before editing; preserve any newer user changes.
3. Address audit R4 next: persist an actionable session-local integrity failure
   and continue eligible unrelated sessions in generation and delivery rounds.
   Do not swallow database-wide storage failures or programming errors.
4. Add generation/delivery isolation regressions, review human-readable errors
   and logs, and update the audit and implementation log.
5. Then address R6 (inspection after ownership release) and stale documentation.
6. Continue docs/phase-1-plan.md: session controls/worker, remaining conditions and
   patterns, production acceptance contract/adapter, acceptance and capacity
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
