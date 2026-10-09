# Version 1.0.0 release specification

Status: Phase 1 scope baseline, October 9, 2026. This specifies release targets;
it does not declare the current build production-ready. It supersedes the original
Phase 1 implementation plan for release sequencing, not the individual model contracts.

Baseline: `ea29007`, with 811 .NET tests, 28 Python tests and independent pattern
checks passing locally. [CI on that commit](https://github.com/jbknepp98/industrial-data-sim/actions/runs/37734742144)
passed Windows, Linux and macOS, including host and synthetic capacity smoke checks.
Those results establish a tested baseline, not production capacity certification.

## Product and architecture

An agent-operated C#/.NET 10 application generates synthetic industrial TVQ data
and publishes it to Timebase Historian. Models are declarative, versioned JSON.
No graphical model editor or human-written executable scripts are required.
SQLite on local disk holds immutable definitions, checkpoints, ownership, queued
payloads, delivery history and horizon revisions. One host owns each database.
Concurrent sessions receive bounded, fair turns; HTTP publishing is serialized.

A session has one connection profile and one Dataset. Sessions can share a
Dataset only with disjoint output tags. Dependencies between individual signals
and equipment groups are supported within a session. Ownership is local to the
state database: multiple databases and external writers must be operationally
assigned disjoint tags. Distributed ownership is not promised.

## Included behavior

- Numeric, Boolean and string outputs; constant, bounded ramp, staircase and
  seeded random holds; sequences and Boolean-triggered branches.
- Gates suppress output while closed and either pause or continue pattern time.
- SKU routing to mutually exclusive packaging cells, readiness interlocks, typed
  conditions, edges, held conditions and prioritized state transitions.
- Seeded noise, freeze and quality faults; explicit same-tick and previous-tick
  dependencies within manufacturing models.
- Elapsed-time totalizers and randomized production-batch totalizers, target
  policies and condition/target termination. Production batches are distinct
  from transport batches.
- Historical generation followed by wall-clock-paced live publishing in the same
  session; deterministic restart; bounded buffering and visible backpressure.
- Admission, status, pause/resume, cancellation, tag release, guarded horizon
  extension, explicit preflight retry and completed-session audit archival.
- Structured JSON errors/status, bounded rotating operational logs, arrival
  observations and separately recorded human pattern review.

Existing generator and manufacturing model forms remain separate; v1 does not
promise arbitrary nesting between them. Finite sequences keep their documented
terminal behavior; extending a horizon does not automatically repeat them.

## Invariants that block release if violated

1. Timestamps strictly increase per tag within and across batches and restarts.
   No historical insert, duplicate replacement or silently shifted timestamp.
2. Durable payload and progress transitions stay transactional. Restart cannot
   reroll randomness, reset model clocks or repeat production events.
3. Failed, paused and uncertain sessions retain required ownership and evidence.
   A session-local model failure does not terminate healthy sessions; actual
   storage failures must remain visible and stop unsafe work.
4. Publishing is blind. HTTP completion, observed arrival and human review are
   different evidence. Missing/repeated samples and count differences never
   authorize replay. Ambiguous writes remain Uncertain for operator investigation.
5. TLS chain and hostname verification remain enabled. No secrets, raw values,
   private configuration or raw exception details appear in ordinary diagnostics.
6. Resource limits reject or pause work with actionable errors; unresolved work
   is never discarded to meet a resource budget. Logs cannot authorize recovery.

## Compatibility and contract freeze

Release candidates must record exact OS, CPU architecture, .NET runtime and
Historian/Pulse build versions tested. Current CI images are ubuntu-24.04,
macos-15 and windows-2022; that matrix alone is not a support claim for every
OS or CPU architecture. Phase 4 selects and publishes the artifact/platform
matrix from clean-machine tests. No untested Historian version range is claimed.
Live acceptance must record the actual stack build without credentials.

The current model/response schema versions, CLI commands, structured error codes,
ordering and recovery semantics form the compatibility baseline. Additive optional
fields are allowed; breaking changes require explicit versioning and migration
notes. Product version, model schema, SQLite schema (currently 8), archive format
(currently 2), and generator identity are distinct. Do not rewrite old generator
identity or downgrade a migrated database to make an old binary accept it.
Version 1.0.0 is assigned only during release packaging, not by this plan.

## Workload targets to qualify

These are engineering acceptance targets chosen for the first release, not
measured guarantees. Phase 2 must record reference hardware, storage, network,
stack versions and settings before timing runs. Any reduction requires an explicit
scope decision and updated acceptance evidence, not a silent test relaxation.

| Target | Required qualification |
| --- | --- |
| Concurrency | Four active sessions, eight output tags each; at least two share a Dataset with disjoint tags |
| Model mix | Simple signals, gated sequences, two-cell SKU manufacturing, and a production-target model |
| Time resolution | One-second observation interval; one-second manufacturing process clock where applicable |
| Historical/live duration | 72-hour backfill followed by seven days live for continuing models; target-bounded models complete normally |
| Backfill recovery | Sustained generation/publish rate exceeds live demand and backlog decreases without starving another session |
| Live freshness target | p95 newest published timestamp lag at most 10 seconds during healthy steady-state operation, excluding deliberately suppressed tags |
| Control target | p95 status/pause response at most five seconds and no request deadline exceeded during healthy reference workload |
| Memory/storage | Bounded queues at configured limits; no unexplained sustained memory growth; measure peak memory, disk growth and required headroom |

Lag is local publish progress, not proof that Historian retained every point.
Measure current-value arrival/change indicators separately. Report sample counts,
percentiles, maxima and workload settings, including failures. Do not exclude
healthy-run latency outliers just because they are inconvenient. Fault windows
have separate recovery expectations rather than the healthy response target.
An absolute supported memory/disk requirement and backfill throughput must be
set from Phase 2 results before release; they are not yet established.

## Current guards versus supported operating limits

| Implemented guard/default | Release implication |
| --- | --- |
| 100 unarchived sessions per worker; 32 production tags per session | Safety ceilings, not a validated production load claim |
| Default transport batch: 1,000 points / 1 MiB | Measure with the real adapter, including preflight and observation overhead |
| Default queues: 100,000 points / 16 MiB per session; 1,000,000 points / 64 MiB global | Both point and byte limits matter; total process memory can exceed queue bytes |
| Default free-disk floor: 64 MiB | Guard only; deployment must budget database, WAL, logs, backups and archives |
| Default manufacturing: 250,000 node-clock and 2,000,000 expression-clock evaluations; 16 MiB estimated observation storage | Preserved for legacy/target-stopping models; opt-in windowed models use bounded ticks and checkpoints |
| Archive export capped at 128 MiB, eligible finished sessions only | No claim of unlimited active-session retention or partitioned archival |
| Up to 1,000 horizon revisions | Extensions remain finite and guarded, not indefinite execution |

Eight nodes over 72 hours at one-second ticks require 2,073,600 node evaluations,
above the default precompiled node limit even before the live interval. Phase 2 introduces
opt-in windowed execution for fixed-horizon models; see its qualification report. It must address
execution/reconstruction costs and safe bounds; merely raising limits is not an
accepted remedy. Preserve deterministic equivalence if introducing checkpointed
or incremental process execution.

Version 1.0 supports finite operating horizons with explicit extension or planned
session rollover. It does not promise indefinite active-session history growth.
Phase 3 must prove a forward-only rollover procedure preserving required process
continuity and archive headroom. If that is insufficient for the qualified
workload, implement bounded archival in portions before release. Never delete
unresolved history or reset a totalizer silently to make rollover work.

## Deferred beyond 1.0

Graphical editing, cross-session/external live input subscriptions, distributed
workers or ownership, arbitrary executable model code, unrestricted physical
models, automatic throughput tuning and unlimited mutable schedules. Archive
import/replay is not a recovery feature; backup restoration has a separate tested
procedure. No exactly-once delivery or per-point acceptance guarantee is claimed.

## Remaining phases and release authority

1. **Scope:** this specification plus the [acceptance matrix](release-1.0.0-acceptance.md).
2. **Capacity:** qualify the workload, address manufacturing execution costs and
   investigate/control latency; publish measured operating limits.
3. **Operations:** failure drills, backup/restore, upgrade, retention and rollover.
4. **Packaging:** clean-machine artifacts, startup templates, compatibility,
   dependency/security review and installation/upgrade runbooks.
5. **Acceptance:** release-candidate 72-hour backfill and seven-day live soak,
   restart/extension drills, arrival observations and user pattern approval.
6. **Release:** final audit, exact-candidate CI and artifact checks, release notes,
   known limitations, approval and immutable `v1.0.0` tag/artifacts.

All matrix gates must pass or have an explicitly approved scope change. Ordering,
recovery, credential safety and data-integrity violations cannot be waived by
calling them known limitations. A repair affecting soak behavior invalidates the
relevant evidence and requires repeating the affected acceptance. Phase 1 closes
with a reviewable scope and gates; it does not authorize starting live writes,
installing services or claiming production readiness.
