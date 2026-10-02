# Session horizon extension — proposed first increment

Status: design only. No extension command or database migration is implemented.
Existing production sessions and their original end times are unchanged.

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

The first implementation step is offline equivalence coverage. No production
extension or migration should be applied during that step.
