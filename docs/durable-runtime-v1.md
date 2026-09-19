# Durable runtime v1

This increment runs finite, deterministic simulation sessions against a durable
local queue and a fake Historian. It does not enable production Historian writes.

## Persisted contract

A session stores the validated, immutable simulation JSON and SHA-256 hash,
generator version, next candidate-slot cursor, total candidate slots, status,
and bounded operational error. A candidate slot is a position on the original
session sample grid plus an output-tag index. Its timestamp is never shifted to
wall-clock time. Suppressed gate slots advance the cursor without generating a
point. All current patterns are pure functions of this cursor and the original
configuration: replaying the definition reconstructs seeded schedules and local
clocks without a mutable random state. New stateful patterns will require a new
checkpoint contract before they can be supported.

Generation in memory is provisional. Persist the exact encoded TVQ batch, its
hash, per-tag generated/buffered positions, and the next cursor in one transaction.
A crash before commit leaves the old cursor; a crash after commit leaves both
payload and checkpoint. No generated checkpoint can outrun its durable payload.

Every database is explicitly marked `simulation-only`. A future production runtime
must refuse these fake acknowledgements; it must not reuse this database as real
delivery evidence. SQLite user_version is the migration boundary. Unknown newer versions are
rejected. Database schema version 2 added a partial covering index containing only
unacknowledged batches. Opening a version 1 database creates the index and advances
its version in one transaction; configurations, payloads, checkpoints, attempts,
and reservations are unchanged. The first upgrade must read existing batch
history to build the index, so large databases may take longer to open. Use a
SQLite-aware backup before upgrading; older runtimes reject version 2. Do not
manually lower the version to bypass this protection.

WAL, synchronous FULL, foreign keys, serialized short transactions,
and a lifetime exclusive owner-file handle protect a local database. Use one
canonical database path on local disk; do not alias it through symlinks or copy
an open database without SQLite-aware backup. Never delete the owner file while
the runtime is running. Local ownership does not exclude external writers.

Tag reservations use connection-profile, Dataset, and tag identity with a
conservative invariant uppercase key. Connection profiles are references, not
credentials. Within a profile, sessions may share a Dataset only for disjoint
tags. Multiple profile aliases for the same server must not be used to bypass
ownership. Keep reservations through pause, failure, uncertainty, and completion
until explicit release. Complete sessions use ReleaseCompleted; safely Cancelled
sessions with no outstanding batches use ReleaseCancelled.
Retain per-tag progress after release to reject backward reuse.

## Session progress and schema version 3

Schema version 3 introduced independent progress; the current schema is version 4.
`Progress(id)` reports that session's own tags
and buffered/submitted/acknowledged positions from `session_tag_progress`.
Every declared tag has a row, with null positions until that session reaches the
corresponding stage. ReleaseCompleted changes only current ownership; completed
progress and original tag display names remain available after release, restart,
and later reuse of the tags. A later session starts with null positions even
when earlier sessions used those same tags.

The separate `tags` table retains global high-water marks and current ownership
for admission and backward-write protection. Session and global positions update
within the same generation, claim, or acknowledgement transaction. A rollback
cannot advance either report independently of its batch/checkpoint. Progress
inspection after release cannot weaken admission checks.

Opening schema version 1 or 2 upgrades transactionally through version 3 to the
current version. The progress upgrade
reconstructs session-local progress from verified immutable tag declarations and
retained batch-position metadata, including acknowledged batches whose payloads
were pruned. It never copies a later owner's global timestamps. All batches
contribute buffered positions; Sending, Uncertain, and Acknowledged contribute
submitted positions; only Acknowledged contributes acknowledged positions.
Tags that never emitted retain nulls. This restores inspection of already released
sessions, provided their required model and audit metadata remain intact.

The first upgrade reads all session definitions and batch-position metadata and
can take time on a large history. Use a SQLite-aware backup before upgrading.
Invalid configurations or unusable metadata stop the upgrade with a safe error;
the migration transaction rolls back without changing the schema version or
partially persisting reconstructed progress. Restore verified state rather than
editing versions or forcing replay. Older runtimes reject newer schema versions.
Long-term archival must preserve these reports or define an explicit archival
contract; deleting audit history before this upgrade is unsupported.

## Session and batch transitions

Sessions begin Ready, may be Paused, become Draining after their last candidate
slot, and Complete after all payloads are acknowledged. All-suppressed output
may complete without a batch. Uncertain and Failed sessions stop generation and
delivery but retain data and ownership. Resume affects Paused sessions only.
RetryGeneration provides the narrowly checked oversized-point recovery described below.

Batches progress Pending -> Sending -> Acknowledged or Uncertain. Persist Sending
and an attempt record before calling the fake transport. On startup convert all
Sending batches to Uncertain and stop their sessions. Never retry an Uncertain
batch or send a later batch for that session. Submitted positions represent
submission intent, not proof of network receipt. Acknowledged positions advance
only with the fake's explicit whole-batch acceptance result. Retained samples are
separate evidence; repeat suppression does not reduce the acknowledged position.

Explicit cancellation adds Cancelling and Cancelled session states. Drain stops
generation while delivering existing queued work; DiscardPending permanently
abandons only unsent payloads, marking their batches Discarded. Both preserve
ordering history and require explicit ownership release. Schema version 4 records
the cancellation mode and excludes Discarded batches from the live queue index.
See [session cancellation](session-cancellation.md) for eligibility, failure handling,
release rules, and operator guidance.

## Isolating a damaged session

Before generation or claiming a queued batch, the runtime validates the saved
configuration hash, generator version, and model. A configuration-integrity
failure persists Failed with the safe `runtime.configuration_integrity` error
and logs the session ID, affected operation, and corrective guidance. Configuration,
checkpoint, batches, progress, and tag ownership remain untouched. No new batch is
claimed or submitted for that session. A previously claimed submission may still
finish; its normal acknowledgement/uncertainty rules remain in force.

`GenerateRound` reports a non-progressing turn for the failed session and continues
eligible peers. A direct `Generate(id)` still throws the structured RuntimeFailure
after saving Failed. Delivery returns no work for the affected claim and continues
the round; inspect session status/logs for the reason. Failed sessions are excluded
from later rounds, including after restart. This is not automatic repair, release,
or replay; Resume and RetryGeneration cannot bypass configuration-integrity failures.

Only the known configuration-integrity failure is isolated this way. If persisting
Failed cannot complete, the storage error propagates and the round stops. Other
storage errors and unexpected programming exceptions are not swallowed. Healthy
sessions remain subject to global queue/disk limits: retaining a failed session's
pending data can still consume shared capacity.

## Recovering an oversized point

A `generation.point_too_large` failure leaves the session Failed without advancing
its checkpoint. To recover, close the runtime, increase `RuntimeLimits.BatchBytes`
(up to 4194304 bytes), and ensure session/global queue byte limits fit a full batch.
Reopen the same database, then explicitly call `RetryGeneration(sessionId)`.
Reopening alone does not resume the session. For example:

```csharp
using var logs = new RuntimeFileLogger("logs");
using var runtime = new DurableRuntime("state/simulation.db",
    new RuntimeLimits { BatchBytes = 2 * 1024 * 1024 }, logs);
runtime.RetryGeneration("session-a");
// Continue the normal generation/delivery loop; do not call AddSession again.
```

Recovery checks the saved failure code, configuration hash/version, checkpoint,
and tag reservations, and rejects any Sending or Uncertain batch for the session.
It previews at most 10000 candidate slots and one emitted point using the current
byte limit. Suppressed slots cannot hide the offending point when the newly
configured generation turn is smaller. The preview is discarded.

Only after these checks succeed does one transaction change Failed to Ready and
clear the resolved error. Configuration, cursor, queued payloads/hashes, attempts,
per-tag positions, and reservations are unchanged. Recovery itself never submits
a batch. A crash before commit leaves Failed; after commit it leaves Ready.
The `generation.retry_enabled` information event follows the commit.

If the limit is still insufficient, `runtime.generation_limit_unresolved` explains
how to adjust it and leaves the original failure intact. Other errors identify
an ineligible status, unresolved delivery, inconsistent checkpoint, missing
ownership, or configuration-integrity problem. Preserve the database and follow
the returned guidance. There is no general reset of Failed or Uncertain sessions.
If the point cannot fit the maximum supported limit, a changed configuration
requires an independent session with disjoint tags; do not edit saved state.
Queue or disk pressure can still delay normal generation after successful recovery.

## Limits and scheduling

Bound configuration bytes, admitted tag count, candidate slots per generation
turn, emitted points, and encoded batch bytes. Bound queued points and bytes per
session and globally. Check available local disk headroom before generation.
If queue/disk limits prevent a turn, leave the cursor untouched and report why.
A candidate that cannot fit an empty batch fails with corrective guidance rather
than stalling forever. Keep only one bounded generation window in memory at a
time. Serialized round-robin turns provide fairness across active sessions;
sessions coexist durably rather than needing one generator thread each.

Global and per-session queue totals read only the `batch_outstanding` index.
Pending, Sending, and Uncertain batches all count against capacity; Acknowledged
and Discarded history does not. Completion checks use the same index. SQLite maintains index
membership atomically with inserts/state changes, including rollback and restart,
so no separately persisted queue counters need reconciliation. The cost of
summing counts depends on outstanding batches, not the retained history size.
This does not bound history storage or eliminate all historical lookups elsewhere.

Acknowledgement removes the payload body after persisting its hash, counts,
range, attempts, and per-tag acknowledgement. Retain batch audit metadata and
checkpoints. WAL autocheckpointing and explicit checkpoint on disposal manage the
log; freed pages can be reused. Database file shrinking and metadata archival
are later operational work. Free-space checks are conservative preflights, not
reservations against other processes filling the disk; database write failure
must leave the transaction uncommitted.

The [session CLI](session-cli.md) exposes bounded inspection and lifecycle controls
while the runtime owner is stopped. It does not start generation or delivery.

## Library usage and observability

Reference `src/IndustrialDataSim.Runtime/IndustrialDataSim.Runtime.csproj` from a
.NET 10 host. This bounded example uses synthetic configuration and fake delivery:

```csharp
using IndustrialDataSim.Runtime;

using var logs = new RuntimeFileLogger("logs");
using var runtime = new DurableRuntime("state/simulation.db", logger: logs);
runtime.AddSession(File.ReadAllText("examples/boolean-gate-simulation.json"));
var fake = new FakeHistorian();
var delivery = new SimulatedDelivery(runtime, fake);
for (int round = 0; round < 100; round++)
{
    var generation = runtime.GenerateRound();
    int delivered = await delivery.RunRoundAsync();
    if (runtime.Sessions().All(s => s.Status == SessionStatus.Complete)) break;
    if (generation.All(turn => !turn.Progressed) && delivered == 0)
    {
        // No generation OR delivery progress. Inspect status, errors, and turn reasons.
        break;
    }
}
```

The round cap bounds this example; inspect status after the loop rather than
assuming that reaching the cap means completion. See [runtime logging](runtime-logging.md)
for readable messages, event codes, retention, safe context, and logger health.

On restart, open the existing database without calling AddSession again. The
original session ID/configuration is immutable. Sessions(), GetSession(id), and
Progress(id) expose status, cursor, queue usage, and session-local per-tag positions. Batches(id,
afterId, limit) supports pagination (maximum 1000 records per call); payload bodies
are available only until acknowledgement. Treat inspection output as local data,
not a public diagnostic dump. RuntimeFailure exposes a stable Error.Code and a
safe explanation; queue/disk backpressure is a GenerationTurn reason, not a crash.

Call Pause/Resume explicitly. Pause prevents future generation/claims; it does
not revoke a write already in flight. ReleaseCompleted is explicit and retains
per-tag history even after releasing reservations. There is deliberately no
"reset Uncertain", replay, edit-config, or force-acknowledge method.

FakeHistorian supports full acceptance, failure before acceptance, loss of a
response after acceptance, partial acceptance, and an ambiguous response. Its
accepted history is distinct from retained history. It keeps test evidence in
memory and is not a capacity model for the real service. Retain the same fake
instance when testing a runtime restart; creating a fresh fake resets the remote
side of that experiment. Normal generation holds one bounded window at a time;
inspection APIs and fake history allocate separately for test/reporting purposes.

## Verified scope and remaining limits

Tests cover all eight existing simulation examples across a database reopen,
transaction rollback/commit boundaries, true child-process termination, ownership
conflicts, pause/resume, fair scheduling under a one-batch global capacity,
cancellation, simultaneous delivery claims, repeat suppression, configuration and
payload hashes, newer schema rejection, and a two-day 17280-point run with restart.

This API is a finite backfill engine driven by an explicit caller. It is not yet a
resident service, real-time pacing loop, or production network client. Production
connection-profile identity must resolve aliases consistently before remote writes
are enabled. Reservations protect only sessions in this database, not other
installations or external applications. No connection credentials are stored.
Current pure generators need only an immutable model and cursor; future production
batch accumulators, external inputs, or mutable rules require richer checkpoints.
Audit metadata remains on disk after payload pruning; long-term metadata retention
and volume sizing are operational work still to implement. Disk FULL does not
promise survival of hardware/filesystem corruption or power-loss semantics beyond
SQLite and the filesystem's durability guarantees.

Implementation references: [Microsoft.Data.Sqlite transactions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions)
and [database errors and concurrency](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/database-errors).
The runtime serializes access to its connection and keeps transport work outside
transactions. Package version is pinned in the project file.
