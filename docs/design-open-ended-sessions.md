# Design note: open-ended sessions (1.1.0)

Status: **approved with owner decisions**, November 21, 2026. See "Owner decisions" below.

## Requirement (owner, November 21, 2026)

A session backfills by a user-set amount. Once caught up, it continues in real
time **until it is stopped**. That is the default. A later, optional setting may
end a session after a duration or at a date and time.

## What 1.0.x does today

- Every session header requires `endUtc`. The runtime computes a fixed slot
  total from `startUtc`, `endUtc` and the sample interval. The session goes
  Complete when its cursor reaches that total.
- Running longer means a guarded horizon extension. Each extension is a durable
  revision, capped at 1,000 per session. Extensions are meant to be deliberate,
  not a way to run forever.
- Live pacing already exists: generation never commits a sample later than the
  wall clock, so a far-future end is no risk of running ahead. The runtime
  simply needs the end to stop being mandatory.
- Three things assume the run will finish:
  1. Generator patterns (`sequence`, `staircase`, `booleanTimeline`,
     `randomIntegerHold`) cover a fixed duration and then `holdLast`. On an
     open-ended run they would flat-line once their schedule ran out.
     Manufacturing `schedule` with `repeat: true` already cycles forever.
  2. Audit history grows without limit. Each published batch leaves a database
     row: about 45 MB for the 75-hour acceptance run. Cleaning up needs a stopped
     host and a paused session (`archive-history`). There is no automatic
     cleanup, by design.
  3. Precompiled manufacturing models, the only kind that can stop on a target,
     are limited to 250,000 node evaluations over the whole horizon. So they
     cannot be open-ended.

## Proposed design

### 1. Model contract

Session header v2 has two changes:

- **`endUtc` is optional.** Leaving it out means the session runs until stopped.
  An explicit `endUtc` keeps today's behavior exactly, so existing models keep
  working.
- **`backfill`** is a new duration setting (for example `"72h"` or `"7d"`), as
  an alternative to `startUtc`. At admission the runtime resolves it to a
  concrete start: now minus the backfill, aligned down to the sample grid. It
  stores that start durably next to the unchanged admitted bytes and their hash,
  the same pattern horizon revisions use. Restarting never re-resolves the
  start, so clocks and seeds stay identical.

Exactly one of `startUtc` and `backfill` is required. Admission refuses a backfill
older than the Dataset's purge age, using the existing preflight settings read,
with a corrective message.

### 2. Runtime horizon

An open-ended session has no slot total. The wall-clock fence that already paces
live generation bounds each round. Status reports:
- `horizon: "open"`;
- the newest published timestamp and the live lag;
- no completion percentage.

Such a session is never Complete. It ends only through:
- the existing cancel (drain or discard pending);
- the later stop-after or stop-at setting, which will reuse the
  horizon-revision machinery to set an end once.

Extension commands refuse open-ended sessions with `extension.open_horizon`,
because there is nothing to extend.

Pause and host stop keep today's meaning: on resume, the session catches up the
gap deterministically and does not skip it. If the gap grows past the Dataset
purge age, the existing retention guard fails the session safely. It does not
drop points. "Skip ahead" stays out of scope.

### 3. Patterns that keep going

Each finite generator pattern gains `"after…": "repeat"` as an alternative to
`holdLast`. Repeating restarts the pattern's local clock. A random pattern draws
each cycle from a seed derived deterministically from its base seed and the cycle
number, so the stream stays reproducible and each cycle differs.

On an open-ended session, admission **warns** about any finite pattern that is
not set to repeat. The warning names the pattern path and suggests `repeat`
(owner decision 3). Finite sessions keep accepting `holdLast`.
Windowed manufacturing already runs tick by tick from a bounded checkpoint, and
`schedule` already repeats, so manufacturing models need no pattern change.

**Example:** the acceptance Utilities and PackagingLine models would switch their
`holdLast` patterns to `repeat` (the shift timeline becomes one 8-hour cycle).
ProcessCell already repeats.

### 4. Bounded storage: automatic history retention

Open-ended sessions need audit history cleaned up while the host runs.
Proposal: a host setting `historyRetention` (default **7 days**, matching the
Test Dataset's purge age). On a bounded timer, the host deletes Published batch
rows, attempt records and observations older than the retention age.

What is never deleted:
- Pending, Sending and Uncertain work;
- cursors, process checkpoints and tag progress;
- the model and its horizon revisions.

Cleanup runs as small transactions between delivery rounds. It never blocks
publishing for long, and each pass is logged once as `history.pruned`.

This deliberately changes the 1.0 rule of no automatic deletion (owner
decision 2 below). The deleted rows are local audit records of what was sent;
Historian keeps the data itself, so nothing is resent or lost. If you want a
permanent audit trail, the existing manual `archive-history` stays available.

### 5. Target-bounded models

Precompiled models with `stopOnTarget` stay finite and unchanged: an open-ended
precompiled model is rejected. Making the windowed engine able to stop on a
target is a separate future change.

### 6. Compatibility

- Database schema 11 adds a nullable effective end and the resolved start. Opening existing state migrates it transactionally; the
  current backup-before-upgrade rule applies.
- Archive format 4 records the open horizon.
- Existing finite models, CLI commands and behavior are unchanged.

## Owner decisions (November 21, 2026)

1. **Backfill is set in the model file:** `"backfill": "72h"`.
2. **No automatic deletion.** Section 4 is withdrawn: all audit history is kept.
   The local database grows with run time, roughly 15 MB/day at the acceptance
   workload (about 45 MB per 75 hours). This scales with points published, so
   documentation will give the estimate and the manual trim path: pause, run
   `archive-history`, then resume. Disk-pressure guards still stop generation
   safely before the disk fills.
3. **Non-repeating finite patterns are allowed** on open-ended sessions.
   Admission succeeds with a warning that names each pattern path that will hold
   its last value, and suggests `repeat`.
4. **Ship as 1.1.0**, with an rc smoke test in Docker on linux-arm64. The owner
   runs the long soak, then the tag.

## Out of scope for 1.1.0

- the stop-after and stop-at setting (designed for, built later);
- skipping ahead after a long outage;
- open-ended target-bounded models;
- editing a running model;
- a Docker image in CI.

## Implementation and acceptance plan

1. **Pattern repeat:** add generator `repeat` with per-cycle seeds. Offline
   tests prove that a repeating pattern's stream equals the concatenation of
   its cycles, that it is identical across window sizes, restarts and long
   runs, and that the random draws differ across cycles.
2. **Header v2 and backfill:** schema and runtime validation, admission-time
   start resolution, the purge-age check, and v1 models still accepted
   unchanged.
3. **Open horizon in the runtime:** schema 11 migration, nullable total,
   status fields, cancel-only ending, and the extension refusal. Equivalence
   test: an open session matches a long finite session slot for slot.
4. **Storage guidance:** document growth per point and the manual
   `archive-history` trim path. There is no automatic deletion (owner decision 2).
5. **Long-run fast test (fake transport):** weeks of simulated time compressed
   into minutes, checking flat memory, predictable database growth, and
   unchanged per-tag streams.
6. **Docs, rc build and smoke:** CI on all four platforms, then a native
   linux-arm64 Docker smoke covering admission from backfill, live, restart,
   pause/resume, and cancel.
7. **Owner soak, then tag `v1.1.0`.**
