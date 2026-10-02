# Two-cell manufacturing demonstration — October 2, 2026

Dataset: `Test`. Tag prefix: `Sim.LennyDemo.20261002T1826`.
Window: September 29, 18:26 UTC through October 2, 18:26 UTC (end exclusive).
One-minute sampling; nine tags; 29,160 generated samples.

This bounded historical demonstration uses the existing `build_model` helper in
`scripts/prepare_packaging_demo.py` with `days=3` and fresh local durable state.
It does not start a background live publisher.

## Layers of behavior

1. `Production.SKU.String` alternates SKU-A and SKU-B every six hours.
2. `Mixer.Ready.Boolean` waits 15 minutes, runs 90 minutes, stops 30 minutes,
   then runs 225 minutes in each six-hour cycle.
3. Each `PackagingA/B.FeedEnabled.Boolean` requires both readiness and its SKU.
   The cells cannot receive product simultaneously.
4. Each cell has two gated speed sequences: `Speed.PauseClock` freezes during
   interruptions; `Speed.ContinueClock` advances silently. Both suppress samples
   while their feed gate is false. Suppression does not write explicit nulls;
   a viewer that carries the last value forward may visually bridge these gaps.
5. Speed sequences alternate randomized-duration 0/10/20 staircases and five-hour
   runs of random integers 21–29, held 15–45 minutes. Fixed seeds make results
   reproducible. The paired clock variants share patterns and seeds.
6. `Mixer.Temperature.C` independently ramps from 20 to 80 at 0.01 degrees/second,
   then remains at its upper bound.

## Checks and review

The independent offline checker passed all 4,320 timestamps, strict ordering,
quality 192, routing exclusivity, speed bounds and closed-gate suppression.
Both cells show 1,633 timestamps where the clock-policy variants differ.
The production run completed 30 batches with an empty queue and no session error.
Immediate batch observations included Observed, ConsistentWithoutNewArrival and
NotYetObserved; those records were preserved. A subsequent read-only comparison
found matching historical evidence, changing values and non-null current values
for all nine tags, with zero mismatches among returned in-window records. Missing
or suppressed samples were not interpreted as delivery failures or replayed.
Human pattern acceptance remains NotReviewed. Arrival indicators are not
per-point receipts. Credentials, certificates and generated state remain ignored.

For a screen demonstration, show SKU and readiness first, then both feed tags,
then overlay one cell's two speed tags around a readiness interruption and a
SKU change. Step-style rendering makes the holds and Boolean transitions clear.


## Backfill-to-live follow-up

The original finite demo cannot be extended by the current runtime. To demonstrate
the supported transition without rewriting its state or replaying its tags, a
fresh model now runs under `Sim.Packaging.20261002T1832-a16929` in Test. It spans
September 29, 18:32 UTC through October 9, 18:32 UTC, with 72 hours of backfill
followed by seven days on the same one-minute simulation grid. The nine patterns
and routing rules are unchanged. The independent 72-hour checker passed again.

One `production follow` invocation performs both catch-up and paced publishing.
At October 2, 18:34 UTC the read-only durable snapshot showed Ready, no error,
31 Published batches and source/feed progress through 18:34 UTC. Readback found
matching historical evidence and non-null current values for all nine tags, with
zero mismatched returned records. Gate suppression explains older speed values
during the initial readiness wait; Cell A is scheduled to start at 18:47 UTC.
These observations do not establish per-point receipt or long-run reliability.

The active local folder is `.tools/packaging-demo-20261002T1832-a16929`.
`active-run.json` records the process identity and command, `follow-live.json`
receives the eventual completion result, and `logs/` retains runtime events.
The publisher runs independently of this conversation while the machine remains
awake. It is not an auto-restarting system service. Verify process identity before
sending SIGINT for graceful shutdown. After exit, inspect durable status; resume
with `production follow` against the same `state.db`, never re-admit or regenerate
this session. Failed or Uncertain state requires inspection, not blind replay.
The finite model ends October 9 at 1:32 PM America/Chicago.


## Additive production speeds

Added `PackagingA.Speed.PackagesPerMinute` and
`PackagingB.Speed.PackagesPerMinute` under the live prefix. Both write explicit
zero while unselected or not ready. Each activation restarts a six-minute,
three-stage startup, followed by seeded 2–5 minute holds: A normally 110–125
packages/minute, B 85–100. Five-minute reduced-speed periods model slowdowns;
the two cells use distinct seeds. These are illustrative rates, not calibrated
plant throughput. Existing PauseClock/ContinueClock tags remain demonstrations
of clock semantics rather than inputs to these new production-speed signals.

The additive session shares the original start, end and sampling grid. Two
`PackagingA/B.SpeedControl.Boolean` helper tags are derived from the saved SKU
and readiness timelines at their exact boundaries. This mirrors the immutable
schedule; it is not a cross-session live subscription. Future edits to another
session would not automatically propagate. Existing tags are never rewritten.
The reproducible builder is `scripts/prepare_packaging_speeds.py`.

Before publishing, independent preview comparisons checked each new speed and
control against the original feed at all 4,320 historical minutes. Each speed
was zero exactly when feed was false; ranges and timestamp alignment passed.
Two Python tests cover the full ten-day control schedule, disjoint tags, input
immutability and an actionable mismatched-duration error. Live readback found
changing, matching historical evidence and non-null current values for all four
new tags, with zero mismatched returned records. Counts are not delivery receipts.

The separate paced publisher uses the `speeds/` subdirectory of the active demo
folder, with its own `state.db`, `active-run.json`, logs and final-result file.
Both publishers must be inspected and gracefully stopped before shutdown; resume
each against its own existing database. The speed session also ends October 9,
18:32 UTC. Neither process automatically restarts after a reboot.


## Controlled live restart — October 2, approximately 20:16–20:17 UTC

Verified both active process commands against their saved identities, then sent
SIGINT. Both returned Stopped, Ready session state, empty queues and no errors.
The original process had published 132 batches and the speed process 116.
Both exited and left fully checkpointed databases without WAL files. SQLite
integrity checks returned `ok` for both.

A normal read-only diagnostic connection failed after shutdown. After confirming
process exit and absence of WAL files, immutable read-only connections successfully
inspected the stopped databases. This diagnostic access failure is not claimed
root-caused; neither publisher reported a shutdown or database error. Immutable
mode was used only while both databases were confirmed inactive and checkpointed.

Resumed each original `production follow` command against its existing database;
no model was re-admitted or altered. Original batch identities, states, ranges
and hashes remained unchanged, as did model hashes. First new batch ranges were
39816–39834 for the original session and 17700–17704 for the speed session,
starting exactly at their stopped candidate cursors. Tag progress did not regress.
Both reached published time 20:17 UTC with Ready state and no session error.
Historian returned non-null current values for all 13 tags. Feed and production
speed were zero during the scheduled readiness interruption.

Local `restart-before.json`, `restart-stopped.json`, `restart-verification.json`
and `follow-pre-restart.json` preserve evidence; `active-run.json` now identifies
the replacement publisher for each database. Both publishers remain running.
This demonstrates graceful live stop/resume and preservation of prior batch
records. It does not prove per-point storage, a forced-crash recovery path, or
long-duration unattended reliability. Those remain separate tests.
