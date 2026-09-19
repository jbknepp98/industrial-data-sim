# Session cancellation

Runtime-library controls are also exposed by the [session CLI](session-cli.md).
A continuously hosted worker remains a future increment. All delivery remains simulated.

Cancellation stops future generation and is irreversible. Callers must explicitly
choose what happens to already generated, queued work:

```csharp
runtime.Cancel("session-a", CancellationMode.Drain);
// Or, deliberately discard unsent data instead:
// runtime.Cancel("session-a", CancellationMode.DiscardPending);
```

There is no default mode. Neither choice undoes values already submitted or
acknowledged, rewinds the generator, or edits the model. Repeating the same accepted
request is harmless while Cancelling or Cancelled. Changing the mode is refused.
If delivery subsequently fails or becomes uncertain, another cancellation request
cannot clear that problem.

## Drain

Ready, Paused, and Draining sessions can choose Drain. Generation stops immediately
under the runtime lock and status becomes Cancelling. Delivery rounds continue
submitting existing Pending batches in order, using the same integrity and
acknowledgement rules as normal delivery. Cancelling is not pausable/resumable in
this initial contract. Callers still need to drive delivery; Cancel does not start
a background worker.

An already Sending batch may finish. Success contributes to draining; an ambiguous
result or restart with Sending work makes the session Uncertain. Its cancellation
mode remains visible in SessionSnapshot.Cancellation, while payloads and ownership
stay protected. Configuration or payload integrity failure stops draining as Failed.
Such failures cannot be cleared by switching to discard mode.

When no outstanding batches remain, status becomes Cancelled, not Complete—even
if the requested generation range was unfinished. A session with no queued work
becomes Cancelled in the cancellation transaction. TotalSlots remains the original
range; NextSlot remains the last committed generation checkpoint.

## DiscardPending

Ready, Paused, Draining, and Failed sessions may choose DiscardPending only when
there are no Sending or Uncertain batches. A Pending batch with any attempt record
also blocks discard: its unsent status cannot be trusted. Complete and Uncertain
sessions cannot be cancelled. These checks and all mutations share one transaction.

The transaction marks Pending batches Discarded, clears their payload bodies,
records the chosen mode, and finishes cancellation. This permanently abandons that
unsent data. Batch IDs, hashes, counts, ranges, position metadata, session progress,
and prior error explanations remain for inspection. Discarded batches consume no
queue capacity and can never be claimed. Acknowledged batches remain acknowledged.
The payload absence of a Discarded batch is not evidence of delivery.

Buffered positions continue to describe what was durably generated, including
later-discarded data. Submitted and acknowledged positions never advance because
of discard. Inspect batch states to distinguish discarded data from delivered data.

## Releasing ownership

Cancelled sessions retain reservations until an explicit release:

```csharp
if (runtime.GetSession("session-a").Status == SessionStatus.Cancelled)
    runtime.ReleaseCancelled("session-a");
```

ReleaseCancelled verifies that there is no outstanding batch. ReleaseCompleted
remains limited to Complete sessions. Both preserve session reports and global
ordering protection. Reusing a released tag requires a start later than its global
buffered high-water mark, including previously discarded points. This conservative
policy does not permit filling discarded gaps or reusing the cancelled session ID.

## Errors and observations

| Code | Action |
| --- | --- |
| `runtime.invalid_cancellation_mode` | Explicitly choose Drain or DiscardPending. |
| `runtime.cannot_cancel` | Inspect status and eligibility; Complete/Uncertain sessions cannot cancel, and Failed sessions cannot drain. |
| `runtime.cancellation_locked` | Inspect the existing cancellation mode and any failure. Do not change modes or try to clear uncertainty. |
| `runtime.cancellation_unresolved` | Wait for an in-flight result or investigate acceptance. Preserve uncertain work; do not replay. |
| `runtime.cancellation_attempt_exists` | Investigate the inconsistent Pending batch and attempt evidence. Its payload cannot safely be discarded. |
| `runtime.cannot_release_cancelled` | Finish cancellation or investigate unresolved delivery before releasing ownership. |

Information events `session.cancellation_requested` and `session.cancelled` explain
the selected behavior and next action; successful events follow commit. Repeated
identical cancellation requests do not repeat these events. `session.tags_released`
records explicit release. Logs remain observations, never recovery authority.

## Persistence and verification

Schema version 4 adds cancellation_mode and updates the outstanding-batch index
to exclude both Acknowledged and Discarded batches. Older schemas upgrade in one
transaction. Building the replacement index reads history; large databases may
take longer to open. Keep a SQLite-aware backup and use the current runtime after
upgrade; do not downgrade the schema marker.

Tests cover drain/discard, paused cancellation, immutable mode, empty queues,
failed generation, capacity release, progress retention, backward-range rejection,
in-flight results, uncertainty, inconsistent attempts, schema migration, and real
child-process termination immediately before and after cancellation commit.
A pre-commit crash preserves the prior session and payloads; a post-commit crash
preserves the entire cancellation outcome.
