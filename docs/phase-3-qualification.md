# Phase 3 — operational recovery and retained history

Date: October 14, 2026. This closes the operational implementation phase under
the recovery policy below. It does not release v1.0.0: clean-machine packaging,
exact-stack compatibility, the seven-day release-candidate soak, user pattern
review and final release audit remain in Phases 4–6.

## Recovery contract

Normal restart uses the newest original database and preserves process identity,
clock, random state, totalizers, payloads and ordering. Sending interrupted at
startup becomes Uncertain and is never replayed automatically.

Loss of newest state is different. A backup cannot reveal writes that happened
later. Disaster restore therefore permanently quarantines all old sessions and
allows new work only under a newly generated tag namespace. It preserves old
payloads and evidence, including original lifecycle/error fields. There is no
same-tag disaster continuation, unquarantine or force-ack shortcut in v1. This is
the conservative engineering default; it does not infer new Historian guarantees
or claim the user approved every regenerated pattern.

See [the full runbook](state-backup-recovery.md). New database schema 10 stores
quarantine evidence; schema 9 introduced archive segments. Old binaries reject
upgraded state. Models and generator identity are unchanged. Archive format 3
adds segment identity/links; older receipts still verify by byte count and hash.

## Completed operational gates

| Gate | Evidence and result |
| --- | --- |
| V07 ordering and deterministic interruption recovery | Full generation, checkpoint, windowed equivalence, horizon and forced-process recovery suites pass. Rolling process history emits byte-identical payload streams versus the unrolled reference. |
| V08 no replay after ambiguous publish | ProductionDeliveryTests cover transport ambiguity and interrupted claim/finish; ProcessRecoveryTests kill processes across durable boundaries. Stale Pending and Sending snapshots restore only as quarantine. |
| V09 useful auth/TLS/read failures | ProductionDeliveryTests cover read-only token refresh, repeated 401, other errors and no POST retries; HistorianTlsTests exercise correct/wrong root and hostname through local TLS. Payloads and safe error guidance are retained. |
| V10 retention and external conflicts | AuditRepairTests recheck changed/elapsed retention, including restart; ProductionDeliveryTests reject external timestamps before claim. Corrected preflight never authorizes uncertain replay. |
| V11 isolation versus storage failure | SessionIsolationTests cover damaged sessions and healthy neighbors; persistence/programming failures remain visible. DurableIntegrityTests verify transaction rollback rather than hiding storage damage. |
| V12 backups, restoration and upgrades | Snapshot verification, source immutability, copied-byte authentication, wrong digest/schema, schema 8/9/10 restore, quarantine mutation refusal, fresh-tag enforcement, metadata damage and old-binary rejection pass. Actual process kills cover restore transaction and publication boundaries. |
| V13 disk pressure, growth and rollover | Low-disk tests preserve queued work and windowed cursor; archive guards retain rows on low disk/damaged export. Process kills before/after segment commit preserve pending data and either original rows or a verified receipt. Measured history workload below demonstrates page reuse and retained archive growth. |

The final local build passed **872 .NET tests**, zero warnings/errors. All
**28 Python tests** passed. Independent checks passed ten simulation examples,
six invalid schema cases, 100 gate scenarios/3,722 emitted samples, and the
72-hour packaging and coordinated-speed oracles. These are synthetic/local
checks; no live Historian data was written and no existing demonstration state
was upgraded. Completed platform CI is recorded below.

The isolated real-CLI upgrade drill built commit `54de022` (schema 8), checkpointed
synthetic work, upgraded it with the current CLI to schema 10, and compared saved
session, tag, batch, attempt, horizon and process-checkpoint evidence. It matched.
The old CLI then returned `runtime.schema_version` without changing database
bytes. Reproduce using `scripts/verify_state_upgrade.py`; the run accepts no
existing database or live configuration.

## Retained-history measurement

Reproduce in a Release build:

```text
dotnet tools/IndustrialDataSim.CapacityProbe/bin/Release/net10.0/IndustrialDataSim.CapacityProbe.dll history-load 10000
```

The `1000` variant is included in CI. The fixed fixture uses four windowed
manufacturing sessions, eight tags each, one-second sampling, eight points per
batch, a sealed fake receiver, and history segments of 1,000 completed batches
per session. Execution is accelerated; no live uptime or network throughput is
measured. Each segment checks unchanged session/progress and empty completed
history, then resumes the same process. Every receipt is verified.

Reference: macOS 26.2 ARM64, .NET 10.0.12, local filesystem. One 10,000-round run:

| Measurement | Result |
| --- | --- |
| Samples / completed batches | 320,000 / 40,000 |
| Verified segments | 40 |
| Database + WAL + SHM after first rollover window | 9,478,304 bytes |
| Database + WAL + SHM after second window | 10,701,920 bytes |
| Database + WAL + SHM after tenth window | 10,726,496 bytes (about 10.2 MiB) |
| Retained archive files | 27,181,749 bytes (about 25.9 MiB) |
| Archive bytes per completed batch | 679.54, including manifests and receipt links |
| Largest measured single-segment archive operation | 305.55 ms |
| Total accelerated elapsed time | 49.58 seconds |

Storage settled after initial allocation with small receipt/checkpoint growth;
this is evidence of page reuse, not proof of indefinitely bounded storage. The
run excludes production observation rows, runtime log growth, backups and large
custom configurations. Timing overlapped local development activity and is an
operational reference, not an isolated throughput benchmark.

At four batches/second, seven days would produce 2,419,200 batches. Applying this
fixture's export bytes/batch projects approximately 1.53 GiB of archives alone;
this is an estimate, not a seven-day measurement. Production observations,
larger models/tag names and retained copies increase that number. Keep the
Phase 2 planning allowance of **10 GiB available storage** for the reference
workload, monitor actual usage, and increase it for other models/retention plans.
The runtime's 64 MiB default floor is not deployment capacity. Archival additionally
requires 256 MiB of headroom on the state volume; separately provision the export
volume if different.

## Operating procedure for finite horizons

Stop the worker/host and pause active sessions before history maintenance. Export
completed history in 1–10000-batch portions, inspect/verify receipts, then resume.
Pending payloads remain untouched. Sending or Uncertain work prevents archival.
Preserve all segments, the final archive if any, receipts and the state database.
Do not delete unresolved work or run VACUUM as an improvised low-disk remedy.

For a longer run, drain known-unsent work, perform history maintenance and use
existing guarded horizon extension while retaining tag ownership. The original
model start, sampling grid, cursor and process checkpoint continue unchanged.
Do not final-archive/release a session intended for extension. This provides
process-continuous **audit-history rollover**, not a new session with reset clocks.
Finite horizons, 1000 revisions, 100 visible unarchived sessions and the 128 MiB
per-export cap remain. Metadata/receipts and external archives still grow.

A maintenance stop is an explicit service window. Recovery afterward catches up
from the same durable cursor. Healthy live latency targets do not include the
intentional stop. The full live release-candidate run must exercise this procedure
with its actual deployment storage and stack.

## Review findings resolved and limits retained

- Stale backups cannot prove later non-delivery: restore is quarantined and
  fresh-namespace-only; partial artifacts cannot run.
- Recovery metadata could otherwise be absent/altered: startup and admission now
  validate namespace shape and its identity checksum, returning a safe integrity
  error instead of allowing old tags or exposing arbitrary metadata.
- Whole-session export alone could hit 128 MiB: bounded segments now permit
  completed-history maintenance without resetting process state.
- Repeated manifests could cause quadratic receipt growth: a segment links only
  its immediate predecessor; final archival references all retained segments.
- Restore activation and quarantine commit together; publication is flushed.
  Crash drills do not prove arbitrary hardware power-loss durability.
- Snapshot publication originally flushed contents without the naming directory.
  It now uses the same directory-flush path as restore publication and archive
  pruning. Redundant verified copies remain necessary.
- An initial capacity fixture used a non-Z UTC string and was rejected before
  admission. The fixture now emits canonical Z timestamps and reports validation
  diagnostics before attempting admission. The rejected run is not counted.
- The local SDK command launcher stalled; direct MSBuild with explicit local SDK
  paths completed. Its root cause was not established; no product behavior was
  changed to mask it.

Platform CI finalized October 19, 2026: implementation commit `5818203` passed
[Linux, macOS and Windows verification](https://github.com/jbknepp98/industrial-data-sim/actions/runs/39316708960),
including the new history-retention smoke test. The measurements above remain
dated October 14. Release-candidate repeat of these gates remains part of final
release qualification even after Phase 3 implementation closes.
