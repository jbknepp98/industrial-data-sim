# Phase 1: Resumable simulation sessions

## Objective

Deliver an agent-operated simulator that runs multiple independent sessions,
generates deterministic data from individual tags and conditional equipment
sequences, and writes ordered TVQ batches to Timebase Historian. An interrupted
process must resume from durable state without silently losing data or moving
backward in a tag's timeline.

This is an implementation plan, not a description of existing functionality.
API connectivity is established; a production simulator has not been built.

## Architecture and boundaries

- C# on .NET 10, one application process, with SQLite on local disk.
- An agent-facing CLI with versioned JSON configuration, JSON Schema, structured
  status output, and machine-readable validation errors. No UI or logic editor.
- Separate modules for configuration validation, simulation, session scheduling,
  persistence, Historian transport, and reconciliation.
- One session targets exactly one Historian/Dataset. Multiple sessions may share
  that Dataset only when their output tag sets do not overlap.
- Support independent tags and groups through the same engine. Groups share
  model state and consume named inputs; individual outputs may drive groups.
- Phase 1 dependencies are within a session. Cross-session or externally sourced
  input tags require a later time-alignment and recovery contract and are not
  silently interpreted as current Historian values during historical generation.
- Begin with one globally ordered HTTP write worker and fair scheduling across
  sessions. Generation can progress for multiple sessions within resource limits.
  Add concurrent requests for disjoint tags only after correctness and throughput
  measurements justify it.

## Non-negotiable rules

1. Every output tag has one owner identified by Historian, Dataset, and tag name.
   Acquire all reservations for a session atomically. Paused, interrupted, or
   uncertain sessions retain them. Release only after pending writes are resolved
   and the session is completed or explicitly abandoned.
2. Output timestamps strictly increase per tag, including across requests and
   restarts. Historical inserts and equal-timestamp replacements are prohibited
   by our engine, even where the server accepts them.
3. Simulation time controls the model. Network speed and HTTP payload boundaries
   do not alter generated values or simulated production batches.
4. Random choices are reproducible. Persist generator algorithm/version, seed,
   stream state, active timers, and model state. Use stable independent random
   streams so scheduling another session does not change existing output.
5. Distinguish generation progress, submitted progress, and verified delivery.
   HTTP 200 is not sufficient evidence that each point was stored.
6. Keep credentials outside model definitions and SQLite. Persist only connection
   profile references; redact secrets from logs and diagnostics.
7. Dataset names contain letters, digits, hyphens, underscores, or ordinary spaces.
   Reject other special characters, empty/whitespace-only names, and surrounding
   whitespace without silently renaming the Dataset. Initially allow ASCII
   letters/digits; this is our conservative validation policy, not a claim that
   the API documents an ASCII restriction. Continue URL-encoding path segments.

## Milestone 1: Model contract and executable foundation

Create the .NET solution, CLI host, test project, versioned configuration schema,
and typed TVQ model. Separate simulation configuration from connection secrets.

Define sessions with an ID, connection profile, Dataset, explicit UTC start,
end condition, seed, sampling rules, resource limits, and declared output tags.
End conditions support a simulation end time or a designated production target.
Pin configuration and generator versions for the lifetime of a job; changed
configurations must not silently resume an old checkpoint.

Validate tag references, value types, finite numeric values, output ownership,
time bounds, sampling intervals, random ranges, rule actions, and dependency
cycles before any writes. Validate uncertain server naming/case semantics
conservatively to prevent two aliases from bypassing reservations.

The runtime library now implements explicit drain/discard cancellation; see
[session cancellation](session-cancellation.md). Agent-facing lifecycle commands now exist in the [session CLI](session-cli.md);
the explicit run-simulated command drives a [bounded foreground worker](simulation-worker.md).
Other session commands admit/control state without executing work. The
[continuous host and live controls](continuous-host.md) now execute simulation work
and service commands while retaining exclusive SQLite ownership. Runtime statuses currently use Ready,
Paused, Draining, Complete, Uncertain, Failed, Cancelling, and Cancelled; the
broader names below are proposed host/model states, not additional implemented states.

Provide commands for validate, dry-run, start, list, status, pause, resume, and
cancel. Model the lifecycle explicitly: Created, Running, Waiting, Paused,
Draining, Completed, NeedsAttention, Failed, and Cancelled. Define cancellation
as stopping generation with an explicit drain-or-discard-pending choice; never
pretend cancellation undoes already-submitted records.

**Exit check:** invalid jobs return precise configuration paths and error codes;
valid synthetic jobs run without network access and emit deterministic TVQs.

## Milestone 2: Small but composable generation engine

Implement these initial primitives rather than the entire future pattern library:

| Capability | Phase 1 behavior |
| --- | --- |
| Individual signals | Constant, ramp, staircase, bounded random integer/real |
| Sequences | Ordered steps containing a pattern and fixed/random timing |
| Noise | Optional bounded uniform additive noise, with explicit type/bounds policy |
| Faults | Timed freeze and quality override, separate from underlying process state |
| Time accumulation | Integrate the configured rate over simulated elapsed time |
| Production accumulation | Add a fixed/random quantity on production-batch completion |
| Groups | Shared state indicator selects behavior for related tags |
| Conditions | Typed comparisons, ranges, nested AND/OR/NOT, edges, elapsed duration |
| Actions | Select behavior, advance/branch, pause/resume, restart, or stop |

Specify fixed or randomized production duration and quantity independently.
Persist in-progress batch state. A target may permit a whole-batch overshoot or
cap the final quantity, as explicitly configured. A target completion stops
generation for the session, then drains its pending records before completion.

Timing supports per-value dwell bounds, whole-step duration limits, or both.
Validate feasibility rather than truncating a hold below its configured minimum.
Fixed-duration random-hold steps must plan durations that fit the total exactly;
random-duration steps draw a reproducible duration from configured limits.
Keep pattern randomization, measurement noise, and injected faults distinct.
Noise may change observations during an otherwise constant underlying hold.

Use simulation events and a deterministic dependency order. Evaluate drivers
before consumers at a timestamp, then emit at most one final observation per
tag at that timestamp. Explicitly delayed feedback may be supported later;
reject instantaneous dependency cycles in phase 1. Record transition times so
time-based accumulation handles rate changes between samples correctly.

**Exit check:** changing delivery batch size, generation window size, or session
interleaving does not change a session's generated timestamp/value/quality stream.

## Milestone 3: Gates, conditional sequences, and explanations

Implement start gates, step-entry gates, and run interlocks as separate concepts.
An upstream-ready condition can keep a sequence Waiting without advancing its
execution timer. Its output policy must specify idle output, held output, or
no samples. Inputs needed to open the gate continue progressing in simulation
time; waiting must not freeze the entire session.

Rules declare priority, evaluation triggers, defaults, and clock behavior.
Define whether changing rules continues, pauses, or resets step duration and
whether returning to a previous behavior resumes or restarts it. Define handling
for unavailable or unacceptable-quality inputs and simultaneous matching rules.
Use consistent same-time input snapshots following dependency evaluation.

Provide rising-edge or explicit rearming behavior so a readiness flag held true
does not accidentally retrigger repeated production cycles. Limit transitions
per timestamp and detect waiting cycles or timeouts; surface the blocking
conditions instead of spinning indefinitely. A wait without a timeout is allowed
but must be visible in status.

Build an example with an upstream readiness tag, a gated staircase, a separate
hold at 20, and a five-hour random-integer step using values 21–29 with holds of
at least 15 minutes. Include optional noise, a timed freeze, and an alternate
rule when another tag equals zero. Explicitly choose whether adjacent random
values may repeat.

**Exit check:** dry-run traces explain inputs, selected rules, transitions,
waiting reasons, active faults, and completion without exposing credentials.

## Milestone 4: SQLite queue and concurrent session ownership

The [durable runtime v1](durable-runtime-v1.md) library implements the finite-model
slice of this milestone: transactional generation, bounded queues, reservations,
round-robin turns, pause/resume, and restart recovery. The [continuous host](continuous-host.md) now supplies resident foreground scheduling.
Future stateful pattern checkpoints remain later increments.

Use versioned migrations and tables for sessions, immutable configurations,
tag reservations, generator checkpoints, batches, delivery attempts, and
per-tag buffered, submitted, and acknowledged progress. Store complete payloads needed for exact recovery.

Commit generated batches and the resulting generator checkpoint together.
Use bounded transactions and serialize database writes. Configure durability
explicitly, including WAL/checkpoint management; keep SQLite on local disk.
One application instance owns the database; refuse a second writer process.

Persist batch transitions such as Pending, Sending, Uncertain, and Acknowledged.
Record Sending before issuing HTTP. After a crash, treat Sending as Uncertain.
Do not hold database transactions open across network calls.

Apply limits for per-session and total queued bytes, point counts, memory, and
disk headroom. Backpressure pauses generation, never drops pending data.
Schedule sessions fairly and expose why they are throttled. Prune acknowledged
payloads under a retention policy while retaining resumable checkpoints and
an appropriate audit summary.

**Exit check:** simultaneous sessions with disjoint tags succeed; conflicting
reservations fail atomically. Paused sessions retain ownership. Queue pressure
and process restarts preserve exact generator state and pending payloads.

## Milestone 5: Historian delivery and recovery

Durable delivery transitions and failure injection are implemented against the
sealed fake Historian. Production authentication, preflight, and HTTP delivery
will follow the owner-approved [blind-publish policy](delivery-recovery.md).
Per-point acceptance receipts are not a prerequisite. The real adapter and arrival
monitor are still unimplemented; fake acknowledgements remain synthetic evidence.

**Recovery policy:** follow the [delivery and recovery decision](delivery-recovery.md).
Historian can omit repeated values. Publish completion, arrival indicators, and
user review are separate evidence; latest stored timestamps are not delivery checkpoints.
No missing sample, including a missing suffix, authorizes replay.


Implement Pulse client-credentials authentication, token renewal, verified TLS,
timeouts, and redacted diagnostics. Read Dataset existence/settings and each
existing tag's latest point before accepting a job's proposed time range.
Reject a backfill that would violate that tag's existing timeline; never silently
shift requested timestamps. Surface retention settings that may affect the run.

Batch TVQs by configurable maximum point count, encoded byte size, and live-mode
flush age. Preserve per-tag order within and across requests. Keep production
batch boundaries separate from transport batches. Begin with conservative limits
that are configuration defaults, not claimed server limits.

Persist the generated payload before submission and record Sending before issuing
HTTP. Record normal request completion as Published, including the expected empty
response, before advancing publish progress. Do not label this verified acceptance.
Use bounded reads to check relevant arrival, non-null values, and model-expected
changes; distinguish constants, plateaus, suppression, and backfill time ranges.
Dataset diagnostics are supplemental only. User feedback verifies intended behavior.
Neither counts nor missing repeats prove per-point delivery. Define separate
production state/observation fields without reusing synthetic acknowledgements.

On timeout, crash after Sending, or an unexpected/unsuccessful outcome without a
proven no-write guarantee, mark the batch Uncertain. An expected empty response
is normal for blind publishing and must not be treated as malformed. Stop delivery for its session,
retain tag ownership and payloads, and allow unrelated sessions to continue.
Read-back may reveal conflicts or confirm retained points but never authorizes
an automatic resend. Pending batches known never to have entered Sending can be
submitted after restart. There is no automatic retry of ambiguous writes.

**Exit check:** injected crashes before submission, during submission, and after
server acceptance but before local acknowledgement must preserve pending data and
ownership, resume only known-safe work, or report Uncertain with an actionable
explanation. Do not promise automatic completion or exactly-once delivery where
the server provides no corresponding guarantee.

## Milestone 6: End-to-end acceptance and measured capacity

Use a deterministic fake Historian for injected failures and explicit test tags
in a development Dataset for live checks. Keep connection details and generated
state out of Git. Never use real process tags for fault experiments.

Required acceptance scenarios:

1. Multiple active sessions, including two sharing a Dataset with disjoint tags.
2. A conflicting session is rejected without partial tag reservations or writes.
3. A multi-day backfill interrupted mid-run resumes to the same expected stream.
4. A gated, conditional sequence resumes during a random hold or active fault
   without rerolling values, restarting timers, or repeating production events.
5. A randomized production totalizer reaches its target under the chosen final
   batch policy, then drains all pending writes.
6. A slow/unavailable Historian and a nearly full queue trigger bounded buffering
   and visible backpressure while preserving data.
7. Expired tokens, ambiguous responses, changed quality, and conflicting external
   writes are handled without violating per-tag ordering.
8. Deliberate older or equal-timestamp output is blocked by the engine before HTTP.
9. Restarted and uninterrupted runs have identical generated stream digests;
   read-back verifies the final test data, not just HTTP statuses.

Measure points/second, payload bytes, request latency, verification overhead,
memory, disk use, queue age, and fairness across sessions. Publish the tested
workload and configuration, not an unmeasured throughput promise.

## Deferred work

No UI, distributed workers, cross-session dependencies, unrestricted executable
scripts in configurations, complete fault library, advanced physical models,
automatic throughput tuning, or Kafka-style infrastructure in phase 1.
The architecture should allow these additions without weakening recovery or
requiring separate engines for individual tags and equipment groups.

## Deliverables

- Runnable CLI and versioned agent-facing JSON Schema.
- SQLite migrations, queue, ownership rules, and recovery implementation.
- Historian adapter with authentication, certificate verification, and batching.
- Synthetic individual-tag, gated-sequence, and production-target examples.
- Automated deterministic/crash tests and a documented live acceptance run.
- Operator/agent runbook for starting, inspecting, pausing, resuming, cancelling,
  and resolving an uncertain session.
- Public repository review excluding secrets, certificates, local databases,
  generated payloads, logs, screenshots, and machine-specific paths.
