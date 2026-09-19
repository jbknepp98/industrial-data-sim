# Implementation log

## Increment 1 — solution foundation and Dataset validation

### Scope and decisions

- Added .NET 10 Core, CLI, and xUnit test projects in one solution.
- Enabled nullable reference checks and treated compiler warnings as errors.
- Added a pure Dataset-name validator and a single offline `validate-dataset`
  command. Deliberately deferred the full session schema until its contract can
  be reviewed as a separate change.
- Added XML documentation and inline comments where the reason matters: no
  silent name normalization, explicit ASCII policy, and no reflection of input
  values into diagnostic output.
- Validation returns a stable code, JSON path, and human-readable message.
  The CLI wraps these in a versioned JSON response and meaningful exit code.
- No third-party runtime dependencies. The test project uses the SDK template's
  xUnit and Microsoft test packages; the unused coverage collector was removed.
- A project-local SDK and caches are ignored, together with build output and
  future runtime database/state files. Credentials and certificates remain local.

### Verification scope

Tests cover allowed names, empty input, surrounding whitespace, path separators,
URL syntax, control characters, Unicode whitespace/letters, and emoji. CLI tests
exercise JSON parsing, status codes, argument errors, and non-disclosure of input.
The command's name validation is local only; no Historian reads or writes occur.

Results: Release build and all 30 tests passed with SDK 10.0.401. Direct process
checks also verified valid, invalid, and incorrect-usage responses and exit codes
0, 1, and 2. The initial test declaration used unsupported attribute arguments;
it was corrected to xUnit member data before the successful run. This host's
sandbox blocked MSBuild IPC sockets, so the successful suite ran with permission
to use local IPC. This was a runner constraint, not a Historian dependency.

### Follow-up scope (implemented in increment 2)

Define a deliberately small versioned session-header contract: session ID,
connection profile reference, Dataset, UTC time range, and declared output tags.
Review its schema and validation behavior before adding generation, SQLite,
network access, or the complete sequence vocabulary. Tag ownership across
sessions remains a later transactional concern, not something name validation
alone can enforce.

## Increment 2 — session header and offline file validation

### Scope and decisions

- Added a typed session header and a strict JSON parser with structured errors.
  Invalid input does not return a partially usable definition.
- Defined one Dataset, a connection-profile reference, explicit UTC start/end,
  session identity/version, and typed output declarations. The time interval is
  start-inclusive/end-exclusive. This is not yet a complete generator schema.
- Added `validate-session`, a JSON Schema, and a synthetic example. The CLI caps
  file size at 1 MiB, uses strict UTF-8 decoding, and does not resolve profiles or
  load credentials. Unreadable files use exit code 3 without exposing paths.
- Reject unknown and duplicate properties rather than ignore misspellings;
  reject case-only tag collisions without changing the stored tag spelling.
- Explicit code comments document the conservative naming choices, parser
  boundaries, input limits, and why unsupported field names are not echoed.
- Kept the existing Dataset validator and JSON diagnostic envelope intact.

### Verification scope

Tests exercise required fields, types, versions, identifiers, Dataset names,
valid/invalid UTC dates, time order, duplicate tags, unknown fields, duplicate
properties, malformed documents, and CLI file errors. An exploratory Unicode
test found that JsonDocument defers decoding unpaired surrogate escapes until
string access. Added a decoding pass inside the parser's error boundary and
regression tests to prevent unhandled stack traces.

Results: all 91 tests passed in Release configuration on SDK 10.0.401. Direct
process checks covered the published example, malformed Unicode, and unreadable
files. An independent Draft 2020-12 validator (development-only jsonschema 4.25.1
with rfc3339-validator 0.1.4) and the compiled CLI agreed on 24 structural cases.
The schema check explicitly enabled date-time validation; installing jsonschema
alone had not enabled its optional date-time dependency. No validation libraries
were added to the simulator runtime. Whitespace patterns in the schema were made
explicit to avoid differences between regex engines.

### Follow-up scope (implemented in increment 3)

Review the header contract before adding one deterministic constant generator
and a bounded offline dry-run. Sampling, seed/model version, and a real executable
configuration must be introduced explicitly rather than silently treating a
validated header as a runnable job. SQLite and network delivery remain deferred.

## Increment 3 — deterministic constants and bounded dry-run

### Scope and decisions

- Added a separate versioned constant-model wrapper around the unchanged header.
  It requires a generator version, one positive millisecond sampling interval,
  and exactly one correctly typed constant per declared output tag.
- Added `validate-simulation` and `dry-run`; parsing reuses the header's strict
  JSON/Unicode helpers. File reading is now separate from execution and output
  so output errors cannot be mislabeled as unreadable configuration files.
- Added typed TVQ records with the verified wire keys `t`, `v`, and `q`. Quality
  is fixed at 192 for this first generator. JSON constants preserve their types
  and numeric spelling after the input document is disposed.
- Generation uses integer ticks and the half-open session range. It computes
  counts before allocating, caps the total across tags at 10000, and derives
  each timestamp from the start. No wall clock, random state, or mutable shared
  engine state is involved.
- Added a bounded UTF-8 serialization buffer: previews exceeding 4 MiB fail
  without printing partial data. This includes escape expansion and line ending.
- Added an executable example, a schema referencing the existing header schema,
  and an explicit contract document. Preview output deliberately contains values;
  diagnostics and validation-only responses continue to avoid echoing input.

### Verification

The Release suite passed all 130 tests. New checks cover scalar type preservation,
exact timestamp boundaries, repeated/interleaved runs, generator declaration
order, point budgets across tags, extreme dates and durations, invalid constants,
missing/duplicate generators, nested diagnostic paths, and oversized escaped
string output. The runtime still has no additional third-party dependencies.

An independent Draft 2020-12 schema validator and the compiled CLI agreed on 19
structural cases, including resolution of the local header schema reference.
Two direct CLI runs of the published example produced identical nine-point
output with the expected timestamps, values, and quality codes. No Historian
connection was opened and no data was written.

### Follow-up scope (implemented in increment 4)

Review the constant-model and sampling contract, then add one deterministic ramp
using the same clock and offline validation approach. Keep sequence logic,
randomness, persistence, concurrency scheduling, and delivery as separately
reviewable increments. This preview engine is not yet a resumable session runner.

## Increment 4 — deterministic numeric ramps

### Scope and decisions

- Added a numeric ramp: start value plus rate per elapsed simulated second,
  with optional clamping bounds. Positive, negative, and zero rates are supported.
- Required finite parameters, ordered bounds, and a starting value inside them.
  Ramps must target numeric tags; pattern-specific fields are strictly checked.
- Renamed the internal implementation entry points to `SimulationDefinition`,
  `SimulationDefinitionLoader`, and `DryRun` now that they support multiple
  patterns. Existing constant JSON, CLI commands, and output semantics remain
  unchanged. Introduced a small immutable generator evaluation abstraction rather
  than duplicating the sampling loop or implementing a plugin system prematurely.
- Kept quality, sample clock, and preview limits unchanged. Ramp evaluation uses
  elapsed time from the session origin instead of accumulating per-sample deltas.
- Arithmetic overflow returns a structured failure with no partial preview;
  bounds do not convert infinity into a plausible observation. Constants retain
  their original JSON scalar representation; ramps explicitly use binary64.
- Added a mixed ramp/constant example, a combined schema, and in-code explanations
  of time origin, precision, clamping, and overflow handling.

### Verification

Focused tests cover ascending/descending/zero-rate ramps, one-sided and equal
bounds, starting on a bound, fractional sampling, values at shared timestamps,
interleaved runs, mixed tag types, invalid configurations, and arithmetic failure
at both the engine and CLI boundaries. Existing constant and parser tests remain
part of the regression suite. No network writes or connection loading are added.

Results: all 161 tests passed in Release configuration on SDK 10.0.401. The
independent schema validator and compiled CLI agreed on 16 structural cases,
including the existing constant example. Two direct CLI runs produced identical
15-point mixed previews with ramp values 10, 12, 14, 15, and 15. Repository-visible
files were checked for configured secrets and accidental local artifacts.

### Additional verification before the next pattern

The suite now passes 165 tests. A fixed-seed test exercises 100 varied ramps
against an independent decimal per-step accumulator, including fractional rates,
different sample intervals, negative values, and clamping. Observed differences
were within the asserted 1e-9 absolute tolerance over the tested process ranges.
This is not a precision guarantee for all binary64 inputs.

Parsing and serialized points remained identical under French, Turkish, and Saudi
Arabic cultures. Separate compiled CLI processes also produced byte-identical
output under UTC, America/Chicago, and Pacific/Auckland timezone settings.

A full CLI preview emitted exactly 10000 points at one-millisecond sampling;
extending the range to 10001 points returned the expected structured limit error
with no partial data. No new production-code defect was found in these checks.
These tests remain offline and do not establish Historian throughput or recovery
correctness, which belong to later implementation milestones.

### Next increment

Add a deterministic staircase with explicit dwell durations and precise boundary
tests before introducing randomized timing, sequences, or faults. Keep later
persistence and delivery work separate; there is still no resumable session runner.

### Live 24-hour verification

Submitted all four current pattern categories to Test using six fresh tags,
1440 samples per tag and twelve ordered batches. Every batch returned HTTP 200;
none was replayed. Full-period read-back verified all expected transitions and
3170 returned records, including types, timestamps, values, and quality 192.
Unchanged repeats were omitted, including string repeats after the first batch.
This prevents individual confirmation of all 8640 submitted samples and means
latest stored timestamps cannot serve as delivery checkpoints. Production code
was unchanged. See [the complete result](live-verification-24h.md).

### Deterministic staircase increment

Added numeric staircase steps with explicit positive whole-millisecond dwell
periods and required `afterSteps: "holdLast"`. Boundary evaluation uses cumulative
integer ticks and binary search with no mutable cursor. Exact boundaries advance
to the next step; the final value holds. The existing session sample grid stays
unchanged, so steps shorter than that grid can be missed. Numeric JSON values
retain their token precision. Duration validation guards cumulative overflow.
Code comments explain these choices; the combined schema, example, README, and
[pattern contract](staircase-simulation-v1.md) document agent-facing behavior.

Verification: all 191 Release tests passed (26 new staircase cases). Coverage
includes before/at boundaries, short steps, repeated and descending values,
large integer preservation, shared-timestamp consistency, interleaved runs,
invalid types and fields, explicit end policy, and duration overflow. A compiled
CLI check produced the expected 24 mixed points and staircase values
`0, 0, 10, 10, 10, 20, 20, 20`. An independent JSON Schema validator accepted the
constant, ramp, and staircase examples. No live writes were made in this increment.

The next design decision is reproducible randomized dwell durations, including
how minimum/maximum durations interact with an overall sequence time limit.
Conditional sequences and durable delivery remain separate later increments.

### Seeded staircase dwell durations

Added `durationRangeMs` with inclusive minimum/maximum integer milliseconds,
required per-generator uint32 `seed` for ranged steps, and optional
`maxTotalDurationMs`. Fixed and ranged dwells can coexist. Planning reserves
later minima before choosing each dwell and rejects infeasible schedules.
The cap covers the scheduled dwells; `holdLast` continues the final value until
session end. It neither loops nor launches a subsequent sequence step.

Moved staircase parsing into a focused loader. Resolved schedules remain
immutable and use the existing exact-boundary evaluator. Per-step SHA-256
choices with rejection sampling are explicitly versioned and independent of
sample frequency, other generators, and mutable random state. Inline comments
and the [staircase contract](staircase-simulation-v1.md) explain byte encoding,
seed behavior, duration reservation, and the sequential selection distribution.
Changing these timing conventions requires preserving existing version behavior.

Verification: all 219 Release tests passed (28 new cases). Tests include 200
seeds within constrained duration ranges, tight-budget minimum holds, mixed
fixed/random timing, independent Python timing vectors, repeatability, shared
sample timestamps, and invalid range/seed/budget configurations. A test-only
collection-expression compilation error was corrected before the passing run.
The independent schema validator and compiled CLI agreed on 16 structural cases;
all four examples passed schema validation. Runtime additionally rejected reversed
ranges and infeasible caps with no partial dry-run data. Two compiled CLI runs
were byte-identical; all 180 staircase samples in the 540-point mixed example
matched a separately computed Python schedule and quality/timestamp checks.
No network writes or new dependencies were introduced.

Next small increment: a bounded random-integer value held for a configured dwell,
with explicit treatment of adjacent equal choices. Conditional sequence control
and persistence remain pending. Exact-duration scheduling that fills a target
period is also distinct from this increment's maximum-duration cap.

### Exact-duration random-integer holds

Added the numeric `randomIntegerHold` pattern with signed 32-bit inclusive value
bounds, an explicit seed, fixed or ranged dwell timing, an exact total duration,
and explicit adjacent-value and end policies. Feasible hold counts are chosen
within the supported 10000-hold limit. Each dwell reserves both the minimum time
and the maximum capacity of later holds, so the final hold is never shortened
or extended beyond its bounds to fit. Impossible plans fail before output.
Resolved random-integer schedules also share a 10000-hold model allocation limit.

The planner uses independent versioned count, duration, and value hash streams;
the existing staircase stream remains unchanged. `requireChange` skips the
previous integer without retrying; `allowRepeat` can extend the visibly unchanged
value across multiple planned holds. The immutable schedule reuses existing
half-open boundary evaluation. `holdLast` after the pattern duration is separate
from the exact scheduled interval. Code comments and the
[pattern contract](random-integer-hold-v1.md) explain these choices and limits.

Verification: all 253 Release tests passed (34 new cases), including 200 seeds
for exact total time and value/dwell bounds, fixed boundaries, final holds,
singleton repeats, full Int32 bounds, reproducibility, independent Python output
vectors, infeasible timing gaps, validation, and aggregate allocation limits.
All five examples passed independent JSON Schema validation. Ten invalid shapes
agreed between the schema and compiled CLI; two infeasible schedules failed the
CLI without partial data. The five-hour example generated 900 mixed points:
all 300 random-tag samples matched a separately computed 14-hold Python schedule,
including timestamps and quality 192. Repeated CLI output was byte-identical.

This increment passed offline checks; no Historian data was written. Next is
sequence composition connecting the staircase to these holds, followed by a
separate tested increment for behavior triggered by another tag's value/state,
as agreed with the user. Durable execution and delivery recovery remain pending.

### Finite sequence composition

Added a numeric `sequence` generator with 1–1000 ordered steps, each owning a
`pattern` that inherits the sequence tag. This first increment supports staircase
and random-integer holds only; nested sequences and conditional controls remain
unsupported. Each child's resolved schedule determines its duration. Randomized
staircases advance at actual completion, not at their maximum-duration cap.

Each child starts at local elapsed time zero. Half-open sequence intervals select
the next child exactly at the handoff; the fixed session sample grid does not
restart or insert boundary points. Required `afterSequence: "holdLast"` freezes
the final scheduled value, even if coarse sampling missed its interval. Child
standalone end policies remain required but do not prevent sequence advancement.
Cumulative duration overflow and shared random-hold allocation limits are checked.
Centralized strict pattern dispatch preserves standalone validation and rejects
child tag overrides. Inline comments and the [sequence contract](sequence-v1.md)
explain timing, completion, resource limits, and the current scope.

Verification: all 277 Release tests passed (24 new sequence/CLI cases). Coverage
includes exact and off-grid handoffs, local-clock resets, actual random-staircase
completion below its cap, repeated seeded children, session truncation, final
values missed by sampling, malformed steps, unsupported nesting/types/policies,
aggregate hold budgets, and cumulative duration overflow. All six examples passed
independent JSON Schema validation; ten invalid sequence shapes failed both
schema and CLI without partial data. The completed eight-hour CLI example emitted
1440 mixed points; all 480 sequence samples matched an independent Python
calculation, including timestamps and quality. Repeated output was byte-identical.
The staircase completed at elapsed 3981804 ms; the five-hour random pattern ended
at 21981804 ms, followed by the final-value hold.

No Historian writes were made. The agreed next increment is a behavior trigger
based on another tag's value or state, starting with clearly defined trigger and
waiting semantics. Durable session execution and delivery recovery remain pending.

### Boolean-triggered sequence branches and live test

Added a Boolean timeline and numeric Boolean switch with explicit restart-on-change
semantics. Source binding is independent of declaration order and restricted to
local Boolean constants/timelines. Actual simulated activation times determine
branch clocks, so off-grid changes remain deterministic. Equal adjacent Boolean
states do not retrigger. Both branches validate up front and share existing
resource budgets. Existing generation and sampling remain pure and immutable.
Inline comments and the [trigger contract](boolean-triggers-v1.md) document
initial activation, interruption, re-entry, scope, and remaining limitations.

Verification: 304 Release tests passed (27 new trigger cases), covering both
states, initial True, completion, interruptions/restarts, repeated equal states,
transitions between samples, constant inputs, declaration ordering, determinism,
invalid references/types/policies, overflow, and unsupported nesting. All seven
examples passed schema validation, and the full live preview matched an independent
Python calculation. The user-authorized 24-hour Test write then completed in
12 batches: 2880 submitted samples, six retained Boolean records, and 36 retained
response records. Every transition and returned record matched with quality 192;
all writes returned HTTP 200 without replay. See the
[live result](live-verification-boolean-trigger.md) for exact names and values.
No credentials or certificate material were included in public examples/docs.

Next behavior choices can build on this tested local dependency: start gates,
edge-only triggers, or explicit completion/interrupt policies. General cross-tag
expressions and durable execution remain later work.

### Boolean gate suppression modes

Implemented the user's confirmed pause-and-suppress and continue-and-suppress
semantics. Both initially wait for the first True; subsequent False periods
produce no points. Pause uses cumulative True time; continue uses elapsed time
since the first True. Returning to True resumes the original schedule without
restarting, buffering, or backfilling. Local source binding remains restricted to
Boolean constants/timelines and independent of declaration order. Immutable open
intervals preserve exact timing even between sample slots. Gates support existing
constant, ramp, staircase, random-integer, and sequence patterns without nesting.

Added an explicit emission predicate separate from nullable arithmetic failure.
Dry-run skips suppressed slots before evaluating values, retains an empty array
for a fully suppressed declared tag, and reports actual emitted pointCount. The
10000 candidate-slot limit remains conservative before suppression, preserving
the existing work bound. Current timestamps and quality 192 are unchanged for
emitted points. Comments explain interval construction, clock arithmetic, and
suppression; the [gate contract](boolean-gates-v1.md), schema, and two-mode example
document initial waiting, completion, limits, and unsupported behavior.

Verification: all 330 Release tests passed (26 new cases). Coverage includes exact
opening/closing boundaries, multiple pauses, continuous advancement, no hidden
records/backfill, all-False output, initial True constants, off-grid timing,
sequence completion while hidden, paused boundaries, preserved seeded schedules,
repeatability, type/reference validation, arithmetic failures, scalar constants,
and candidate-work limits. Eight examples passed independent schema validation;
six invalid gate shapes agreed between schema and CLI. The compiled CLI's
20-second example matched a hand-calculated reference: 20 Boolean points plus
11 points from each gate, exactly 42 emitted points. False-gated responses serialized
as empty arrays, not nulls; repeated output was byte-identical. Whitespace checks
passed. No Historian writes were made in this increment.

The requested feature increment is complete. No additional feature work or broad
retrospective review was started; the user plans to request a full code and
documentation review from the beginning next.

### Human readability and actionable-error review rule

Added repository-wide instructions in `AGENTS.md` requiring human-readable code,
comments explaining intent and invariants, and useful errors with stable codes,
structured locations, clear explanations, and corrective guidance. The rule
applies to every returned error, including future delivery and persistence
failures. It also requires bounded diagnostics, explicit truncation, and safe
context without secrets or raw exception details. README links the process.

This is a process/documentation change, not a claim that all existing errors
meet the new standard. For example, the current file-read error says only that
the configuration file could not be read; it should also suggest checking file
existence and read permissions. Apply this standard during the pending A1 repair
and subsequent error-path reviews. No runtime behavior changed in this update.

### Audit repairs and reproducible checkpoint

Fixed bounded diagnostic collection and unified bounded CLI serialization. At
most 100 diagnostics plus one actionable truncation notice are returned; failure
counts still increase after truncation so invalid child patterns cannot pass
construction checks. Responses use UTF-8 byte accounting and an LF terminator.
File-read errors now suggest checking existence and permissions; unsupported
properties direct callers to the version 1 schema without echoing input names.

All 337 .NET Release tests passed, including seven new diagnostic-limit cases.
Corrected active clock/capability documentation. Replaced the unsafe absent-suffix
retry instruction with a conservative Uncertain policy; API acceptance guarantees
remain a prerequisite to the production writer. No writer was implemented.

Versioned readable offline schema/clock verification and read-only live-evidence
comparison, with prerequisites documented. Eight Python verifier tests pass.
All eight simulation examples validate, six malformed shapes fail both validators,
and 100 independent gate scenarios check 3722 samples. Saved full read-backs match
3170 and 42 retained records respectively; omitted repeats are not certified as
individual deliveries. No network requests or Historian writes were performed.

A clean export of the staged repository restored from the existing NuGet cache,
built, and passed all 337 .NET tests, eight Python tests, schema comparisons, and
100 independent clock scenarios. SDK and Python dependencies were supplied
externally as documented; no ignored project source/helper files were copied.
Repository-visible files passed checks for configured secret values, private-key
markers, Markdown links, and whitespace. This is a targeted secret check, not a
guarantee against every possible form of sensitive content. The repair checkpoint
captures the previously uncommitted implementation and these audit repairs.

### Acceptance-contract review and actionable diagnostics

Read the local Historian OpenAPI document over verified TLS. Its write operation
specifies only HTTP 200 with description "OK"; it does not document atomicity,
partial acceptance, durable receipts, or idempotency. Recorded the evidence and
five precise questions in the delivery recovery decision. Production acceptance
semantics remain unresolved; no write/failure-injection experiment was performed.

Reviewed current offline error messages and improved vague range, duration,
pattern, file-size, and encoding failures. JSON syntax failures now expose only
safe one-based numeric line/byte positions, never raw exception messages or
input-derived parser paths. Unicode errors include repair guidance. Suppressed
a misleading secondary range-ordering error caused by invalid numeric bounds
falling back to zero. See the error-review document for scope and limitations.

Verification: all 346 Release tests passed, including nine new actionable-error
cases. Existing simulation tests remain unchanged. Stable error codes, JSON paths,
exit codes, and generator semantics are preserved. Diff whitespace checks passed.

### Durable-runtime increments: state, ownership, buffering, recovery, fake delivery

1. Defined the versioned persisted contract in `durable-runtime-v1.md`: immutable
   model/hash, original-grid cursor, explicit states, durable batches, attempts,
   and separate per-tag buffered/submitted/acknowledged positions. Pure current
   generators reconstruct schedules from the model and cursor, with no rerolling.
2. Added a separate Microsoft.Data.Sqlite runtime project, schema migration v1,
   WAL/FULL durability, lifetime owner file, serialized transactions, atomic tag
   admission, pause/resume, and explicit completed-session release. Initial
   ownership/reopen/concurrent-admission tests brought the suite to 352 passing.
3. Added bounded generation windows and transactional payload/checkpoint commits.
   Queue points/bytes, candidate slots, encoded payload size, and disk headroom
   produce explicit backpressure without advancing progress. All eight examples
   matched original preview output across reopen; 365 tests passed.
4. Added restart handling and injected commit-boundary failures. Persisted Sending
   becomes Uncertain on reopen. Configuration/payload integrity checks prevent
   altered state from silently regenerating or submitting data. A separate
   test-only child process is killed without cleanup at six boundaries; cross-
   process ownership is also checked. The suite reached 386 passing tests.
5. Added delivery solely against a sealed in-memory fake with explicit acceptance
   semantics. Tests cover lost responses, partial acceptance, ambiguity,
   cancellation, concurrency, unrelated-session progress, per-tag ordering, and
   repeat suppression. Payload pruning commits together with acknowledgement;
   hashes, positions, and audit metadata remain. No production HTTP adapter or
   credential path was added.

This increment uses library APIs rather than extending the existing two-argument
validation CLI. Documentation includes a bounded driver example and explains
caller scheduling, local ownership scope, inspection, and future hosted-worker
requirements. No Historian data was written. Runtime tests require only local
SQLite files and synthetic model data.

Final working-tree verification: all 393 Release tests passed (47 new cases over
the prior checkpoint), including real process termination and the two-day
17280-point bounded run with a midrun reopen. Injected SQL failure rolled back
payload, checkpoint, and buffered positions together and returned a redacted,
actionable storage error. Simulation-only database identity is persisted and
checked so fake acknowledgements cannot be adopted as production evidence.
Repository-visible files passed configured-secret/private-key-marker checks,
Markdown link checks, and diff whitespace checks.

A clean export of the staged repository restored from the local dependency cache
and passed the same 393 .NET tests, eight Python verifier tests, all example/schema
checks, and 100 independent gate scenarios (3722 samples). The clean copy included
the crash probe and all required build inputs, with no ignored helper source.

### Operational logging and human-readable troubleshooting

Added optional standard `ILogger<DurableRuntime>` injection and a bounded local
JSON-lines file sink. Events have UTC time, stable codes, plain-language messages,
next actions, and selected session/batch/cursor context. Information covers
lifecycle changes; warnings cover pressure and uncertain delivery; errors explain
failed operations; Debug adds batch/window detail. Successful lifecycle events
follow commits. Repeated unchanged pressure warnings are suppressed. Logging
neither owns recovery state nor authorizes replay.

The file sink rotates with configurable size/count limits, serializes concurrent
writes, and excludes arbitrary formatter messages, scopes, and exceptions.
Initialization/write failures expose logger health and one safe stderr notice
per outage; subsequent events can recover. Injected provider exceptions and
broken stderr cannot change generation or acknowledgement outcomes. Logs contain
no configuration, payload, tag values/names, dataset/profile names, or raw paths.
Neutral session IDs are intentionally retained for correlation. The host must
opt into logging; the existing JSON CLI is unchanged.

Generation failure messages now persist zero-based output-tag index, failing
candidate slot, and UTC sample time, with matching structured log context.
The original checkpoint remains unchanged on a failed window. Oversized-point
advice now explains that Failed sessions cannot resume; the same-tag repair
lifecycle remains unresolved. Corrected the documented driver to consider both
generation and delivery progress before stopping, with regressions for a full
queue and already-Draining sessions. These changes address audit R2/R5; the
remaining findings are tracked in the runtime audit follow-up.

Updated AGENTS.md to require readable, actionable, bounded, privacy-reviewed
logs and tests proving logger failures do not change durable behavior. Added
runtime-logging.md with setup, event meanings, troubleshooting, retention, and
honest limits (synchronous I/O, possible missing/partial records, opt-in setup).

Verification: all 413 Release tests passed (20 new cases), including existing
child-process crash/recovery tests. Eight Python verifier tests, all example/schema
checks, six invalid-shape checks, and 100 independent gate scenarios (3722 samples)
passed. Repository-visible text checks found no configured secret/private-key
matches or broken local Markdown links; diff whitespace checks passed. No
production delivery or Historian writes were performed.

### Audit R1: explicit oversized-point generation recovery

Added `RetryGeneration(id)` for Failed sessions whose saved error is
`generation.point_too_large`. Reopen with larger batch/compatible queue limits,
then request recovery explicitly. The operation verifies the immutable model,
checkpoint and reservations, rejects Sending/Uncertain submissions, and previews
the next emitted point within a bounded 10000-candidate window. Suppressed slots
cannot hide the oversized point when the new turn size is smaller. Only status
and the resolved error fields change, in one transaction; queued payloads,
hashes, attempts, ownership, and progress remain untouched. No transport call is
made. Successful recovery logs an actionable event after commit.

Updated the returned oversized-point guidance, recovery runbook, logging guide,
and audit follow-up. Other failure types remain protected; this is not a generic
reset, configuration edit, or delivery retry. Queue/disk backpressure still
applies after recovery. R3, R4, R6, and unrelated documentation cleanup remain.

Verification: 426 Release tests passed (13 new cases). Coverage includes exact
stream equivalence after recovery/reopen, unchanged queued payloads and positions,
insufficient limits, both injected recovery commit boundaries, configuration and
ownership damage, invalid checkpoints, other failure states, suppressed-slot
lookahead, and in-flight/uncertain delivery rejection. Existing real-process
crash tests also passed; the new recovery boundary tests inject exceptions and
reopen the database. Repository-visible secret-marker/local-link checks and diff
whitespace checks passed. No Historian writes, commit, or push were performed.

### Audit R3: queue accounting independent of acknowledged history

Added schema version 2 with a partial covering index for outstanding batches.
Global/per-session queue sums and completion checks explicitly select that index.
Acknowledged history remains available for inspection without participating in
queue accounting. SQLite maintains the index inside existing transactions;
there are no new counters or reconciliation paths. Version 1 databases upgrade
atomically on open. The documented initial index build reads existing history;
this increment does not implement audit-history retention or promise constant
cost when the outstanding queue itself grows.

Verification: all 434 Release tests passed (eight new cases). Tests examine the
actual global and per-session query plans with 1000 and 100000 acknowledged rows,
verify totals across two sessions, preserve existing state through v1 migration,
and check generation/acknowledgement rollback and commit boundaries. Sending and
recovered Uncertain batches remain charged against queue capacity. Existing
real-process recovery tests passed. No production throughput claim is made from
these deterministic plan checks. No Historian writes, commit, or push performed.
R4 session-failure isolation is the next audit repair.

### Audit R4: isolate session configuration-integrity failures

Resumed from the clean shutdown checkpoint. Generation and delivery now validate
models through a helper that persists Failed and a safe error before isolating
configuration-integrity failures. GenerateRound returns a non-progressing turn
and serves remaining sessions. Direct Generate retains its RuntimeFailure
contract; delivery declines the affected claim before Sending or transport.
The model, cursor, queued batches, per-tag progress, and reservations are retained.
An already claimed submission retains its normal completion/uncertainty handling.

The catch is restricted to runtime.configuration_integrity. Failure to persist
the Failed status propagates as a storage error; unexpected exceptions still
abort the round. One contextual error log identifies the session and operation
and gives recovery guidance without exposing configuration data. No generic
reset/replay path was added. Shared resource limits still apply to healthy peers.

Verification: 443 Release tests passed (nine new cases). Tests cover corrupted
hashes, incompatible generator versions, invalid saved models, generation and
delivery isolation, persisted failure after reopen, unchanged work/ownership,
redacted single-event logging, refused resume/retry, storage failure while saving
Failed, and an unexpected programming exception. The first baseline test request
was not executed because permission review timed out; the retry and final full
suite both passed against the repaired tree. Documentation and restart guidance
now identify R6 as the next repair. No Historian writes, commit, or push performed.

### Audit R6: preserve independent session progress

Added schema version 3 with session_tag_progress, initialized with every declared
tag and null positions. Generation, claim, and acknowledgement update session
and global positions together in their existing transactions. Progress(id) now
reads session-local positions and original display names independently of current
ownership. Release does not erase the report; later tag reuse starts empty and
cannot alter the earlier session's report. Global high-water marks still prevent
backward admission.

Versions 1/2 upgrade transactionally by reconstructing positions from verified
immutable tag declarations and retained batch-position metadata. Acknowledged
payload pruning does not remove this metadata. Sending/Uncertain positions remain
submitted rather than acknowledged. Never-emitted tags retain nulls. Unusable
required metadata or configurations fail safely and roll back the upgrade;
there is no fallback to another session's global positions. Documented the
migration cost, backup guidance, and compatibility boundary.

Verification: 451 Release tests passed (eight new cases), including release/reuse
with case changes, restart, migration of released history and suppressed tags,
submission-versus-acknowledgement semantics, and rollback on damaged history.
Existing generation/delivery transaction and real-process recovery tests passed.
Corrected the session-header, sequence, and staircase documentation drift. R1–R6
are addressed; the next Phase 1 area is operational session controls and a worker.
No Historian writes, commit, or push performed in this increment.

### Session lifecycle: explicit drain/discard cancellation

Added Cancel(id, mode) with required Drain or DiscardPending semantics. Drain
stops generation, persists Cancelling, and delivers existing queued work through
the unchanged claim/acknowledgement protections before becoming Cancelled.
DiscardPending refuses Sending/Uncertain work and Pending batches with attempt
evidence, then atomically marks unsent batches Discarded and clears their payloads.
Neither mode resets progress, undoes writes, or changes an accepted cancellation
mode. Failed drain/uncertain outcomes remain blocked for investigation.

Cancelled sessions retain ownership until ReleaseCancelled verifies no outstanding
work and explicitly releases tags. Session progress and global high-water marks
remain, including buffered positions of discarded data. Schema version 4 adds
cancellation_mode and updates the live queue index to exclude discarded history.
The upgrade is transactional; older models and batches do not acquire cancellation
intent. Added readable lifecycle logs and documented error codes, recovery rules,
mode eligibility, conservative tag reuse, and the library-only scope.

Verification: 465 Release tests passed (14 new cases). Coverage includes drain
across reopen, paused/empty sessions, explicit/immutable modes, discard capacity,
retained audit metadata/progress, ownership and backward-range checks, in-flight
success/uncertainty, failed generation, inconsistent attempts, v3 migration, and
real child-process termination on both sides of cancellation commit. Existing
migration, integrity-isolation, ordering, and recovery regressions also passed.
No Historian writes, commit, or push performed. CLI lifecycle commands and the
continuously hosted worker remain subsequent increments.

### Agent-facing session lifecycle CLI

Added the simulation-only `session` command group: help, start (admission only),
paginated list/batches, status, pause, resume, explicit cancellation, release, and
checked oversized-point recovery. Start validates the bounded UTF-8 model before
opening state. Other commands require an existing database and open it without
SQLite's create flag. Every command honors exclusive ownership and normal startup
migration/recovery. No generation worker or network transport is launched.

Responses use one bounded JSON envelope, readable state names, UTC progress
positions, and actionable runtime/usage errors. Runtime list inspection now has
bounded ID pagination. Batch inspection omits payload bodies at the SQL read as
well as in the response. Logs are bounded files beside the database and remain
separate from stdout. Documented command exit codes, pagination, startup effects,
local inspection data, mutation-before-output-failure behavior, and the fact that
retry-generation limits are not persisted for a later worker.

Verification: 478 Release tests passed (13 new cases). Coverage includes durable
CLI lifecycle transitions, validation before database creation, invalid numeric
arguments/modes, missing state, owner conflicts, list/batch pagination, payload
exclusion, generation-retry protection, readable UTC progress, uncertainty, and
bounded output failure. Seven additional process-level commands exercised the
built executable through admission/list/status/pause/resume/cancel/release; their
temporary simulation state was removed. No Historian writes, commit, or push
performed. A bounded generation/simulated-delivery worker is the next increment.

### Bounded generation and simulated-delivery worker

Saved and pushed the prior 478-test audit/lifecycle/CLI checkpoint as edfdf0c.
Added SimulationWorker and the explicit session run-simulated command. Runs are
limited to 1–10000 rounds and 100 total database sessions, with one bounded window
and one batch per session turn and a rotating first session. No-progress rounds
stop with guidance instead of spinning. Outcomes distinguish Completed, Blocked,
RoundLimit, and Stopped; unfinished/stop CLI exits are 4/130. Failed and uncertain
sessions retain their protections while eligible peers continue.

Ctrl+C requests a graceful stop: finish an in-flight submission under normal
acknowledgement/uncertainty rules, then stop future work and close the database.
The initial control workflow is stop/inspect-or-control/restart; resident hosting,
live IPC, real-time pacing, and production transport remain unimplemented.
The default fake keeps only latest accepted/retained points per tag to avoid
accumulating the full backfill history. Its remote-side evidence remains volatile;
new CLI invocations do not reconstruct it or replay old synthetic acknowledgements.
Documented limits, stop behavior, ownership, memory/evidence boundaries, and logs.

Verification: 490 Release tests passed (12 new cases), covering fair progress with
one-batch global capacity, bounded continuation, paused/disk-blocked stopping,
uncertainty isolation, graceful in-flight stop, overlapping-run refusal, limited
fake history and ordering, session/round bounds, drain cancellation, unexpected
failure propagation, and CLI stop/reopen outcomes. A built-executable SIGINT smoke
test returned exit 130 and reopened state without uncertainty; temporary state was
removed. No production Historian writes were performed.

### Continuous host, live controls, and full audit — September 19, 2026

Completed the three authorized steps following e0f584f: documented the continuous
host/local-control contract, implemented resident foreground simulation execution,
and added live CLI controls. The host retains the sole SQLite owner and runs one
bounded worker round at a time. Controls execute between rounds; idle/blocked
polling waits 250 ms and logs only changed worker states. Scheduling rotation
persists across repeated one-round calls. Ctrl+C and host stop finish in-flight
work and preserve durable checkpoints. All acknowledgements remain synthetic.

Local controls use a same-user named pipe, versioned bounded JSON frames, one
connected client/queued command, deadlines, and explicit unknown-outcome guidance.
No command is automatically retried or falls back to opening SQLite. Admission
validates the supplied model contents and enforces the 100-total-session host cap
before reserving tags. Existing pause/resume, cancellation, release, and checked
generation-recovery protections are reused. The host never reads a remote client's
model-file path or offers a production transport switch.

The full review found and repaired a macOS pipe-name length failure, a broken-peer
cleanup failure that prevented subsequent controls, lifecycle/cursor diagnostic
gaps, and unchecked batch-position metadata. Positions now validate before claim
and acknowledgement and old-schema migration rejects duplicate keys. A failure
after transport leaves Sending for conservative Uncertain recovery. No schema or
generator version changed. Corrected active capability and pattern-clock documents;
retained historical log/report entries and added the current audit report.

Final verification: all 520 Release .NET tests passed in a clean source export
restored from the existing dependency cache. No ignored source or helpers were
copied. Tests include lost control replies, stalled-client graceful shutdown,
invalid controls, inventory bounds, scheduling rotation, state corruption, and
existing process-crash/recovery/ownership and logging-isolation coverage.
The repository process verifier passed against separate host/client processes,
including live peer completion, pause across restart, exclusive ownership, control
stop, and Ctrl+C. The final clean-copy run also used Python -O to confirm checks
remain active. Eight Python tests, all eight example/schema checks, six invalid
shape comparisons, and 100 independent gate scenarios (3722 samples) passed.

NuGet reported no vulnerable direct or transitive packages for the five projects
at review time; Python dependencies were not separately audited. Targeted public
source secret/private-key-marker, local-link, and whitespace checks passed. No
Historian writes were made. See [the full audit](audit-2026-09-19.md) for remaining
production-contract, capacity/retention, control-outcome, CI/platform, and richer
model concerns. Development stops here for the user's review.

### Owner clarification: blind-publish production contract

Recorded first-in-wins duplicate handling and the owner's explicit decision to
use blind publishing without per-point success receipts or count reconciliation.
Normal HTTP completion (with an empty body) will become publish progress, separate
from arrival observations and user review. Timeout/interrupted submissions remain
Uncertain and never automatically replay. The production adapter's state/mode and
monitor details must be designed before changing the current fake-only runtime.

Read the supplied public documentation. The linked validation article concerns
Atlas; Historian's sizing guide documents change-based storage, its diagnostics
provide Dataset activity signals, and its REST guide supports latest/range reads.
Documented the conflict between generic older-point guidance and prior local
observations without overriding the owner's forward-only rule. Updated active
planning/restart/CLI guidance and added a superseding audit note. Documentation
only: no runtime changes, new Historian requests, or live writes.

## CI and initial capacity measurements — September 19, 2026

Added a pinned-action Linux/macOS/Windows CI matrix for Release build, 520 .NET
tests, Python tests, schema/gate oracle, separate-process host checks, and a
synthetic capacity smoke. Windows uses the portable host-stop command; its console
signals are explicitly untested. All three hosted jobs passed for commit `ee058f4`
([CI run 35452466245](https://github.com/jbknepp98/industrial-data-sim/actions/runs/35452466245)).

Added a standalone bounded capacity probe with four concurrent seven-day sessions,
constant/gated patterns, delayed fake delivery, queue-bound assertions, expected
point counts and payload-pruning checks. Twelve local runs passed. Unsupported
peak-memory counters report null; sampled working set and managed memory are
reported separately. See [measurements and retention requirements](capacity-and-retention.md).
No production writer, archive/delete command, runtime tuning, or secrets were added.
