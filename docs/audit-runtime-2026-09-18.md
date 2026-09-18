# Code and documentation audit through durable runtime — September 18, 2026

Reviewed commit `8767d87`, including Core generators/loaders, CLI boundaries,
SQLite state/ownership/queue/recovery, the fake transport, tests and crash probe,
schemas, examples, verification scripts, and active/historical documentation.
The working tree was clean at the start. This is an audit, not a repair pass:
implementation and existing tests were not changed, and no Historian writes
were performed. Findings below are new relative to the earlier audit/repair.

## Findings

### R1 — Medium: raising a batch limit cannot recover the failure that recommends it

Locations: `src/IndustrialDataSim.Runtime/DurableRuntime.Generation.cs:34`,
`src/IndustrialDataSim.Runtime/DurableRuntime.cs:165`,
`src/IndustrialDataSim.Core/Simulation/GenerationWindow.cs:65`.

An oversized single point becomes `generation.point_too_large` and permanently
sets the session to Failed. Its message recommends increasing the byte limit,
but reopening with a larger limit leaves it Failed. Generate will not run,
Resume accepts only Paused, and ReleaseCompleted cannot release its tags.

Reproduced with the constant example, BatchBytes=128, and a string value of 100
`<` characters. After the error, reopening with BatchBytes=4096 still returned
Failed; generation made no progress and Resume returned `runtime.cannot_resume`.
The original configuration remains immutable and queued work/ownership remain
protected, so this is a recovery and troubleshooting defect, not observed loss.

Recommended repair: distinguish a recoverable generation-limit block from an
uncertain delivery. Permit a narrowly validated retry after changing runtime
limits, without altering the model, cursor, queued payloads, or reservations.
Never extend this permission to Uncertain batches. If terminal failure is the
intended policy, provide an explicit safe lifecycle and accurate guidance instead
of suggesting an action that cannot resume the session. Add a regression that
fails at the small limit, reopens with a sufficient limit, and resumes exactly.

### R2 — Medium: the documented driver can stop while useful delivery is happening

Location: `docs/durable-runtime-v1.md:88`.

The loop ignores RunRoundAsync's delivered count and stops whenever generation
made no progress. With a full queue, generation correctly pauses; delivery then
frees space, yet the loop exits. With only Draining sessions, the generation list
is empty and All(...) also returns true, so remaining batches can be abandoned
by the driver after one delivery round. The exact short example normally fits a
single batch; the defect appears when applying its control flow to queued work.

Reproduced with a three-point batch/queue limit and one pending batch: the loop
exited after one round with status Ready, cursor 3/9, and an empty queue. Six
candidate slots remained runnable.

Recommended repair: use both generation and delivery progress for the stop test,
and distinguish completion, backpressure, paused sessions, and failure. Test the
published driver against a prefilled queue and several Draining batches.

### R3 — Medium: queue accounting gets slower as acknowledged history grows

Locations: `src/IndustrialDataSim.Runtime/DurableRuntime.Generation.cs:63` and
`src/IndustrialDataSim.Runtime/DurableRuntime.cs:188`.

QueueUsage aggregates the batches table with `state!='Acknowledged'` on every
generation turn. SQLite reports `SCAN batches`; acknowledged rows are retained
indefinitely, so even an empty queue becomes increasingly expensive to inspect.
Per-session queue sums also revisit historical rows. Bounded queued payloads do
not bound this bookkeeping cost, which matters for the intended long backfills
and many active sessions.

In an isolated synthetic database with no pending payloads, 200 global queue
queries took 39 ms with 10000 acknowledged rows and 512–550 ms with 100000 rows.
These are local diagnostic timings, not production capacity measurements. Query
planning confirms that the growing audit history participates in the scan.

Recommended repair: keep transactional queue counters or separate/index the live
queue so accounting depends on outstanding work. Preserve acknowledged audit
metadata independently. Verify rollback/restart consistency of any counters and
add a history-growth regression or reproducible performance check.

### R4 — Medium: a session-specific integrity error aborts the whole round

Locations: `src/IndustrialDataSim.Runtime/DurableRuntime.Generation.cs:10` and
`src/IndustrialDataSim.Runtime/DurableRuntime.cs:208`.

LoadModel throws RuntimeFailure on a saved configuration hash/version mismatch.
GenerateRound does not isolate that failure: it exits its enumeration and leaves
the bad session Ready, without saving a session error. The delivery round can
similarly throw when claiming a batch for that session. This differs from payload
hash failures, which set Failed and allow the round to move on.

Reproduced by changing only session A's saved config_hash in a temporary database
containing A and healthy B. The first round threw `runtime.configuration_integrity`
and B remained at cursor 0. Calling the round again after catching the exception
allowed B to reach 9 because the in-memory ordering rotated, but the round still
threw and A remained Ready. This is an isolation/status defect; it is not permanent
starvation if a caller catches and explicitly reschedules every failure.

Recommended repair: record a safe terminal/blocking status for session-local
integrity failures and continue eligible unrelated sessions. Keep database-wide
I/O failures distinct; do not indiscriminately swallow storage or programming
errors. Test both generation and delivery round isolation.

### R5 — Medium: new generation errors lose the information needed to locate the fault

Locations: `src/IndustrialDataSim.Core/Simulation/GenerationWindow.cs:45` and
`:78`, `src/IndustrialDataSim.Runtime/DurableRuntime.Generation.cs:36`.

Non-finite arithmetic and oversized-point errors have path `$` with no tag index
or offending sample position. The runtime persists only code/message. The window
already knows the exact tag and elapsed sample time, but that context is discarded.
An error in one of hundreds of tags therefore requires unnecessary searching.
The existing dry-run arithmetic diagnostic has an output-tag path, so durable
execution provides less context than the offline command.

Reproduced a validly configured overflowing ramp among three output tags. The
stored message was only "Generator arithmetic produced a non-finite value.
Reduce the range, start value, or rate." It did not identify the tag or sample.

Recommended repair: retain a schema-safe output/generator index, candidate cursor,
and UTC sample time in structured error data. Avoid echoing values or untrusted
names. Include the effective byte limit for oversized points and persist the
structured context with the session error. Test multi-tag failures and redaction.

### R6 — Low: releasing ownership removes completed-session progress from the public API

Locations: `src/IndustrialDataSim.Runtime/DurableRuntime.cs:146` and `:177`.

Progress(id) queries tags WHERE owner=id. ReleaseCompleted sets owner=NULL, so
Progress on a completed session immediately returns an empty list. Global tag
high-water marks remain in SQLite, and backward reuse is still rejected; no
ordering safeguard was lost. However, the completed session no longer exposes
its final per-tag positions through the documented inspection API. A later owner
also sees the inherited global high-water marks rather than a distinct per-session
progress record.

Reproduced a completed three-tag session: Progress returned three entries before
release and zero afterward, while status stayed Complete.

Recommended repair: separate current reservation/global high-water marks from
immutable per-session final progress, or explicitly expose both concepts through
the inspection API. Test inspection after release and subsequent tag reuse.

## Documentation drift and known limits

- `docs/session-header-v1.md:6` still calls durable resource budgets and tag
  reservations future work. Those now exist in the runtime library. Scope the
  statement to the header validator and link the runtime contract.
- `docs/sequence-v1.md:80` and `docs/staircase-simulation-v1.md:114` describe the
  pure generators as not persisted/resumable. That is defensible for the generator
  itself, but should link the runtime that now persists their execution.
- The missing production API acceptance contract, lack of a hosted worker/real-time
  pacing, local-only reservation scope, profile-alias assumptions, and indefinite
  audit-metadata retention are explicitly documented limitations. They are not
  newly discovered regressions. The simulation-only marker and sealed fake keep
  this runtime from issuing production writes.
- Model parsing and schedule reconstruction happen on every generation turn and
  delivery claim. This is a performance risk to measure before high-volume use;
  no representative capacity benchmark establishes its practical limit yet.

## Verification performed

- All 393 existing .NET Release tests passed, including six real child-process
  termination boundaries and cross-process owner exclusion.
- All eight Python read-back-verifier tests passed.
- Eight simulation examples and the header/subset schemas passed; six invalid
  gate shapes were rejected by both schema and CLI.
- 100 independent gate-clock scenarios passed, checking 3722 samples.
- Saved live-test evidence rechecked: 3170 and 42 retained records respectively;
  omitted repeats were not claimed individually delivered. No new server writes.
- Temporary synthetic SQLite reproductions confirmed R1–R6, including the query
  plan/timing evidence. No real session database was edited.
- All 96 tracked files passed configured-secret/private-key-marker checks and
  relative Markdown-link checks. This is targeted scanning, not a guarantee
  against every possible secret representation.
- NuGet's vulnerability query reported no vulnerable direct or transitive packages
  for the five solution projects at audit time. Python dependency vulnerabilities
  were not separately audited.

The passing tests support the exercised ordering and transactional recovery
paths, but do not cover the findings above. No new high-severity data-loss or
replay defect was reproduced in this audit. Prioritize R1/R2, then R3–R5 and the
inspection/documentation cleanup before adding production delivery.

## Follow-up: operational logging increment

The original findings above describe commit `8767d87`. The subsequent logging
increment adds lifecycle/pressure/delivery logs with safe troubleshooting context.
R2's example now checks both generation and delivery progress, with regression
coverage for a full queue and already-Draining sessions. R5's generation failures
now expose tag index, candidate slot, and UTC sample time; the contextual message
persists across reopen, and log fields preserve the same context without tag
names or values. No schema migration was required.

R1's misleading advice was corrected to explain terminal failure and the need
for disjoint tags in an independent replacement. Same-tag repair/retry remains
unimplemented, so R1 is not closed. R3, R4, R6, and unrelated stale documentation
remain open. Logging improves visibility; it does not resolve those behaviors.
See [runtime logging](runtime-logging.md) for the implemented scope and limits.

## Follow-up: R1 oversized-point recovery

R1 is resolved by the explicit `RetryGeneration` operation. Reopening with larger
limits does not itself change status. The operation verifies the immutable model,
checkpoint, retained ownership, lack of unresolved submissions, and that the next
emitted point fits the current byte limit before atomically returning Failed to
Ready. It changes no generation/delivery positions or queued batches. Ineligible
failures and insufficient limits retain their prior state and return actionable
errors. See the [runtime recovery contract](durable-runtime-v1.md#recovering-an-oversized-point).
R3, R4, R6, and the previously listed documentation drift remain open.

## Follow-up: R3 queue accounting

R3 is resolved with schema version 2's partial covering `batch_outstanding` index.
Global/per-session totals and completion checks explicitly use that index;
acknowledged rows remain in the audit table but never participate in these sums.
SQLite maintains index membership within the same batch transactions, avoiding
separate counters. Version 1 databases upgrade transactionally without changing
payloads, checkpoints, attempts, or ownership. The initial index build still reads
history, and long-term metadata retention remains separate work.

Regression checks exercise the actual accounting query plans against 1000 and
100000 acknowledged rows, assert use of the live index, and verify exact totals
for two sessions. Migration, rollback/commit, Sending, and restart-to-Uncertain
checks protect accounting correctness. These checks establish query access paths
and correctness; they do not claim a production throughput measurement.
R4, R6, and previously listed documentation drift remain open.
