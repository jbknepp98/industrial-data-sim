# Session horizon extension

Current implementation: `session extend` and `production extend` are available.
Use `session horizon` or `production horizon` to inspect revision and effective end.
Stop any foreground publisher before using these direct database commands.

```
production extend <database> <session-id> <endUtc> <expected-revision> <request-uuid>
```

The UTC end must end in Z and include another sample on the original grid.
Reuse a request UUID only with identical arguments. Extension does not publish,
repeat finite schedules, reset clocks, add tags or change model seeds. Ready,
Paused and unreleased Complete sessions with empty queues may extend. Uncertain,
failed, cancelled, archived and released sessions cannot extend. Production
publishing still runs its existing preflight/conflict and retention checks.

Archives now use format 2, including the original admitted definition, effective
horizon, full revision history and the revision used for every batch. The existing
checksum-based verifier remains compatible with format-1 receipts and exports.
No revision history is pruned from the session database. Forced child-process
kills before and after the revision commit verify all-or-nothing recovery.

October 6 production acceptance used fresh Test tags under
`Sim.Horizon.20261006T235027-1d5cf3`. Both historical windows published, the final
ramp read back as 119 at the expected new final timestamp, and the completed
archive verified. The first batch was Observed; the second was
ConsistentWithoutNewArrival (constants were suppressed). No write was retried.
These are arrival indicators; user pattern review remains separate.

## Original design and implementation history

The following design explains the invariants. References to internal-only APIs
or blocked archives describe the earlier foundation and are superseded above.

## Problem and scope

A finite session currently completes at its immutable `session.endUtc`. Resuming
its process does not extend that range. The October 2 demonstration therefore
needed a fresh tag set to show backfill followed by live operation.

The first extension increment should allow a later exclusive end on an existing
session while retaining its original start, sample grid, output tag order, random
seeds, generator definitions, identity, ownership and candidate cursor. This is
an operational horizon change, not arbitrary model editing or indefinite service
hosting. It must work in simulation first and then in production under the same
recovery guarantees.

Adding tags, changing sampling frequency, changing seeds, appending sequence or
SKU steps, and replacing generator algorithms remain outside this increment.
Cross-session controls and automatic schedule repetition remain out of scope.

## Keep the original definition immutable

Preserve the admitted configuration bytes and their existing hash. Add a durable
horizon revision history rather than editing `sessions.config` or its hash.
Revision zero refers to the admitted end. Each later revision records:

- Session identity, sequential revision and caller-supplied request identifier.
- Expected prior revision, previous end and strictly later requested UTC end.
- Unchanged configuration hash and generator version.
- Candidate cursor and prior/new total slot counts at the change.
- Prior/resulting session state and timestamp of the committed operation.

Store the active revision and operational end with a migration that initializes
existing sessions to revision zero. Model loading must verify the original hash,
validate the revision chain and cross-check the active end and total slot count.
Only then construct the effective in-memory session with the extended end. The
original pattern objects and simulated time origin remain unchanged.

Generation, recovery, status, scheduling and archival must use the same effective
horizon. Updating only `total_slots` would violate today's integrity check in
`DurableRuntime.Generation`; updating only the configuration would destroy the
admitted-model identity. Neither is an acceptable implementation shortcut.

The revision history provides integrity/audit evidence, not a cryptographic
security boundary against someone who can rewrite the entire database.

## Sample-grid and pattern behavior

All candidate positions retain their existing meaning: timestamp index multiplied
by tag count, then tag declaration index. Extension never resets `next_slot`.
The end is exclusive and total slots still use ceiling division of the duration
by the sampling interval. An old end between sample times must not introduce a
new point at that end; the next point stays on the original grid.

Require the new end to add at least one candidate timestamp. A later timestamp
within the same final sampling interval otherwise creates an operational change
with no additional work. Reject it with the earliest end that would add a sample.
Calculate with checked arithmetic and retain current timestamp/slot limits.

Extending the horizon does not extend a pattern's finite schedule. For example,
a `stringTimeline` with `holdLast` will continue its last SKU; it will not alternate
again. A completed sequence likewise follows its existing terminal behavior.
A ramp remains bounded, a paused gate retains its accumulated active time, and
seeded holds keep the same draws. Extension must report terminal hold behavior
so an operator does not mistake it for repeating production.

A later, separate schedule-append design will be needed to continue alternating
SKU cycles beyond a finite timeline. It must prove the old stream is unchanged;
horizon extension alone cannot provide that behavior. Do not automatically append
steps during an extension or reroll a schedule against the longer duration.

## Admission and lifecycle rules

Acquire the existing exclusive runtime/database ownership before applying changes.
For the first version, stop a production follow process gracefully, extend, then
resume it. There is no new live IPC or process-control shortcut.

Permit Ready, Paused and Complete sessions only when their queue is empty and no
Pending, Sending or Uncertain batch remains. All declared tags must still be
reserved by that exact session. Reject released ownership, archived sessions,
Failed sessions, cancellation/draining states and unresolved delivery. Do not
reclaim a released tag even if no other session currently owns it. Do not use
extension to clear a failure, cancellation or uncertainty.

Ready remains Ready. Paused remains Paused. Complete becomes Ready after a valid
extension; the command itself performs no generation or publishing. Historical
Published/Acknowledged batch records, per-tag progress and observations are
unchanged. New batches should record the horizon revision used for generation;
legacy batches map to revision zero without changing their payload hashes.

Commit the revision, active horizon, new total and state transition in one SQLite
transaction. Rollback leaves all four unchanged. A crash after commit must load
the complete new revision without requiring the caller to repeat a mutation.

Require the expected current revision to prevent stale requests. Reusing the same
request identifier with identical arguments returns its original receipt without
another revision; different arguments with that identifier fail. An unrelated
request for the already-current end is not a second extension. Status exposes
both the admitted end and effective end, active revision and finite completion.

## Production delivery remains unchanged

An extension is a local state operation and never sends TVQs. The existing
production connection binding, retention checks and pre-claim current-value
conflict detection must still apply before additional publishing. Review whether
a long stopped interval falls outside current dataset retention; do not silently
shift the start of unpublished work or infer acceptance from missing values.

A failed preflight keeps the new horizon and unsent cursor intact with actionable
status. It does not roll back previously published points or authorize replay.
After extension, `follow` catches up known-unsent slots and respects its UTC fence;
`run` retains its explicitly unpaced behavior. Missing readback never authorizes
retrying an old batch. Archive/export must include admitted configuration and all
horizon revisions so a reviewer can reconstruct every generation range.

## Troubleshooting contract

Errors must identify the session and the specific revision/end field, distinguish
rejected state from corrupt state, and include an actionable correction. Proposed
codes include `extension.stale_revision`, `extension.no_new_slots`,
`extension.unresolved_delivery`, `extension.ownership_released`,
`extension.archived`, `extension.invalid_state` and `extension.integrity`.
No raw configurations, credentials, database paths or exception text in messages.

A successful receipt includes old/new ends and totals, unchanged cursor, revision,
resulting state and terminal-pattern notices. One bounded operational event is
emitted after commit. Logging failure cannot change the transaction outcome.

## Implementation and acceptance order

1. Prove generator equivalence offline: compare one long admitted definition with
   the same patterns generated to an earlier end and continued on the original
   cursor under a later horizon. Include gates, switches, seeded holds, sequences,
   aligned/unaligned boundaries, saturation and all-suppressed tails. Compare
   TVQ streams, not transport batch counts or sizes.
2. Add the versioned migration, revision validation, atomic mutation and status
   fields behind the runtime API. Test stale requests, idempotency, overflow,
   ownership, every rejected lifecycle state, queue safety and transaction faults.
3. Add horizon-aware archive schema/verification and backward compatibility for
   revision-zero databases and old archives. Reject corrupt revision chains.
4. Expose simulation/production CLI commands only after the preceding checks pass.
   Document graceful-stop requirements, safe retries and hold-last semantics.
5. Use new synthetic tags for a bounded live extension test, including graceful
   restart around the commit. Keep the current demos unchanged. Confirm arrival
   indicators and obtain user pattern review separately from publishing evidence.

The first implementation step now has offline equivalence coverage in
`HorizonContinuationTests`: 160 pattern/grid/boundary combinations plus three
focused cases. It compares per-tag TVQ streams using independently reloaded
short/long definitions and different generation window sizes. Cases cover seeded
holds and staircases, bounded ramps, sequence and switch clocks, both gate modes,
closed-gate output suppression, SKU routing, partial timestamp rows, repeated
extensions, terminal holds and unaligned ends. A known independent random vector
checks that a mid-hold cut does not restart or reroll the schedule.

Those 163 tests establish the generator prerequisite. Separate durable tests now
exercise schema migration, persisted revisions and transaction/reopen behavior.
Archive revision export and an extension CLI remain unimplemented.

## October 6 implementation boundary

Schema version 7 adds an active horizon revision on sessions, a generation
revision on batches and a bounded revision-history table. Existing records remain
revision zero; admitted configuration bytes/hashes are unchanged. Older binaries
reject version 7. Opening an existing database with this build applies the
transactional migration, so preserve a verified backup before an operational
upgrade. The live demonstration databases were not opened by the new runtime.

Internal `ExtendHorizon` and `Horizon` methods are available to simulation tests,
not CLI callers. Production mode rejects mutation. The internal API implements
UTC/later-end checks, at least one new sample, original tag ownership, empty
queue/state guards, a 1000-revision bound, UUID request idempotency, stale-revision
rejection, model/revision integrity and unchanged-cursor generation. Complete
becomes Ready; Paused stays Paused. A revision and its receipt commit together;
retry after an injected post-commit failure returns that same receipt.

The first implementation infers the expected prior revision from the sequential
revision number, retains generator version on the immutable session, and derives
the effective end from the validated revision history. The admitted configuration
is reloaded with only its end changed in memory. New batches carry their horizon
revision without changing existing payload hashes. Status is internal-only;
public status/receipts and detailed terminal-pattern notices remain CLI work.
The no-new-slots diagnostic currently explains the required grid boundary rather
than printing the earliest allowable end timestamp.

Extended sessions cannot be archived yet: `archive.horizon_unsupported` prevents
the old export format from omitting horizon history. Revision-zero archives
continue to use the existing format. The next increment must implement and test
horizon-aware export/verification before exposing extension through commands.

All 759 .NET tests passed locally, including 26 durable revision tests. Existing
legacy migration tests caught a version-three reconstruction ordering problem:
that step must load the admitted model before version-seven tables exist. The
fix and regression coverage preserve upgrades from older schemas. An injected
schema conflict proves the entire version-seven migration rolls back, including
new columns and user_version. Mutation fault tests use exceptions at transaction
boundaries plus reopen; forced child-process kills at those new boundaries are
not yet covered. Runtime fault logging and error text expose no raw metadata.
