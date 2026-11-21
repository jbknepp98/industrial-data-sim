# Open-ended sessions and backfill (1.1.0)

A session that backfills a set amount of history, then keeps publishing in real
time until you stop it. Design and owner decisions:
[design-open-ended-sessions.md](design-open-ended-sessions.md). Example:
[examples/open-ended-simulation.json](../examples/open-ended-simulation.json).

## Session header, schemaVersion 2

```json
"session": {
  "schemaVersion": 2,
  "sessionId": "line-1",
  "connectionProfile": "local-development",
  "dataset": "Test",
  "backfill": "72h",
  "outputTags": [ ... ]
}
```

- **`backfill`** is a whole number followed by `s`, `m`, `h` or `d`, from 1
  second to 3650 days. It replaces `startUtc`; use one or the other.
- **Leaving out `endUtc`** makes the session open-ended. With an `endUtc`, a
  version 2 session behaves exactly like version 1.
- **Version 1 headers** are unchanged: `startUtc` and `endUtc` are required.

### How backfill is resolved

The CLI resolves `backfill` once, when it reads the model file. That covers
`validate-simulation`, `dry-run`, `production start`, `production-live start`
and `session start`. The start becomes now minus the backfill, aligned down to
the sampling interval, and the admitted model stores that concrete `startUtc`,
so restarts never move it. Loading a model with an unresolved `backfill` other
than through the CLI fails with `session.backfill_unresolved`.

Keep the backfill inside the Dataset's purge age (seven days for `Test`). The
existing retention guard fails a session safely, before publishing, if its
oldest pending data is already past purge.

## Running until stopped

- **It never runs ahead of the clock.** An open-ended session never generates a
  timestamp later than the current time, in any mode, including an unpaced
  `production run`. It catches up its backfill, then follows real time.
- **It never completes.** End it with `production-live cancel <db> <id> drain`
  (or `discard-pending`). Pause, resume and host restart work as before. After a
  pause or outage it fills the gap rather than skipping it.
- **It can't be extended,** because it has no end: `extension.open_horizon`.
- **Manufacturing models must use `execution: "windowed"`.** Precompiled and
  target-bounded models need an `endUtc`.
- **Status shows a very large `totalSlots`.** That's the internal far-future
  end; use `production-live horizon`, which reports `openEnded: true`.
- **`dry-run` previews only the first part** of an open-ended session: as many
  timestamps as fit the 10000-slot preview limit.

## Keeping data moving

Finite patterns hold their last value when their schedule ends. On an open-ended
session, `validate-simulation`, `production start` and `production-live start`
return a `simulation.pattern_holds_last` warning for each pattern that will stop
changing. The session is still accepted. Set those patterns to `repeat`; see
[repeating patterns](repeat-patterns.md).

## Storage

All local audit history is kept (owner decision): the state database grows with
the number of points published. As a guide, the 1.0 acceptance workload used
about 45 MB for 1.09 million points (roughly 15 MB per day at that rate). To trim
it, pause the session and run `production archive-history`; see
[audit archival](audit-archival.md). The free-disk floor stops generation safely,
keeping all work, before the disk fills.
