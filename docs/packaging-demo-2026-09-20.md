# Two-cell packaging demonstration

This demonstration writes nine fresh synthetic tags to `Test`. One upstream mixer
feeds Packaging A for string SKU `SKU-A` or Packaging B for `SKU-B`. Both routes
use the same readiness and SKU sources, so both cells cannot be enabled together.
All dependent tags belong to one durable session.

## Review window and tags

- Prefix: `Sim.Packaging.20260921T0557-081f4b`
- Session: `packaging-20260921T0557-081f4b`
- Backfill: September 18, 2026 05:57 UTC through September 21, 2026 05:57 UTC.
- Pacific local time: September 17 at 10:57 PM through September 20 at 10:57 PM.
- Scheduled exclusive end: September 28, 2026 05:57 UTC (September 27 at 10:57 PM Pacific).
- Sampling: one minute on the original grid; no closed-gate sequence samples.
- Local artifacts: `.tools/packaging-demo-20260921T0557-081f4b/` (ignored).

Append these suffixes to the prefix:

| Suffix | Meaning |
| --- | --- |
| `.Production.SKU.String` | Six-hour alternating SKU-A / SKU-B schedule |
| `.Mixer.Ready.Boolean` | Product available to the selected cell |
| `.Mixer.Temperature.C` | Bounded 20–80 °C warmup ramp |
| `.PackagingA.FeedEnabled.Boolean` | Ready AND SKU-A |
| `.PackagingB.FeedEnabled.Boolean` | Ready AND SKU-B |
| `.PackagingA.Speed.PauseClock` | Cell A sequence freezes while its feed is disabled |
| `.PackagingA.Speed.ContinueClock` | Cell A sequence clock continues while output is suppressed |
| `.PackagingB.Speed.PauseClock` | Cell B sequence freezes while its feed is disabled |
| `.PackagingB.Speed.ContinueClock` | Cell B sequence clock continues while output is suppressed |

Speed values represent illustrative units/min. Pause/continue are alternative
control-policy demonstrations with identical schedules/seeds, not two physical
measurements of the same cell. View each FeedEnabled Boolean beside its speed
signals: the Historian may retain a last current speed or connect trend points
through periods where the simulator deliberately emitted nothing.

Each six-hour SKU interval begins with 15 minutes unavailable, then 90 minutes
ready, 30 minutes unavailable, and 225 minutes ready. SKU changes begin another
15-minute wait before enabling the other cell. Each speed sequence alternates
0 → 10 → 20 staircases (random first two dwell durations, 30-minute hold at 20,
no more than two hours total) with five hours of random integers 21–29 held
15–45 minutes. Schedules are long enough for the full ten-day model.

The Dataset reported seven-day retention during preflight. Older demonstration
history will age out under that setting; no Dataset setting was changed.

## Verification and observation finding

The full offline preview checked 4320 one-minute routing timestamps, 29160 emitted
points, unique increasing per-tag timestamps, quality 192, exact source schedules,
closed-gate suppression and speed bounds. Each cell has 1633 active sampled
minutes where the two clock policies differ. These are offline checks, not
Historian acceptance counts.

The initial paced run reached current time and stopped at its two-minute duration
limit with 32 Published batches, no pending samples and no session error. A second
invocation continued from the same checkpoint, published two additional batches,
and stopped gracefully on SIGINT with no queue remaining. The checkpoint advanced
from 38961 to 38979; the session/model identity remained unchanged.

Initial arrival observations included false Mismatch reports. Read-only diagnosis
of batch 2 showed a prior SKU point at the global batch start, one minute before
that tag's own next submitted point. Because a batch can split a timestamp row,
comparison must use each tag's interval rather than the outer query interval.
The observer now does so. It also uses a matching leading value as a baseline for
change detection when repeat suppression omits the beginning of the new batch;
that baseline never counts as new arrival. Both cases have regression tests.

A separate full-range Historian read found no differing timestamp-matched values
or qualities across all nine tags and found actual changing values. Returned
records are partial evidence; suppression and read behavior prevent acceptance
by point count. Original observation records are retained, including the old
classification errors. They are not rewritten or used to authorize replay.
After the fix, a resumed run published two more batches: one NotYetObserved
and one ConsistentWithoutNewArrival, with no new Mismatch. The final read-only
snapshot at September 21 06:20:47 UTC found matching historical evidence for all
nine tags, all current values non-null, and zero differing observed samples or
unexpected timestamps in the 72-hour interval. These are arrival indicators only.
User review remains NotReviewed until the user inspects the intended patterns.

The final background invocation uses the same database and a 604800-second
invocation limit; its finite model ends first at the scheduled time above.
`active-run.json` contains its PID/command, `follow-live.json` its eventual exit
response, and the runtime's rotating logs show ongoing publishing. Those local
files stay ignored. The process was started explicitly; no system service or
reboot restart was installed.

## Run and restart

The prepared model and production database must stay together. Do not rerun
preparation/admission for an interrupted session. Stop the active publisher
before invoking commands that open its database. The local launch record contains
the process PID and command; verify the PID still belongs to this invocation
before sending SIGINT. Do not use SIGKILL or delete ownership files.

From the repository root, after the old process has stopped:

```sh
TIMEBASE_PROFILE=local-development python3 scripts/local_production.py \
  --dotnet .tools/dotnet/dotnet --environment-file .env \
  status .tools/packaging-demo-20260921T0557-081f4b/state.db packaging-20260921T0557-081f4b

TIMEBASE_PROFILE=local-development python3 scripts/local_production.py \
  --dotnet .tools/dotnet/dotnet --environment-file .env \
  follow .tools/packaging-demo-20260921T0557-081f4b/state.db 604800
```

Inspect any Failed or Uncertain status before continuing. No automatic replay,
service installation, reboot restart or indefinite extension is configured.
`follow` catches up any downtime and then waits for due samples until the
invocation limit or model end. The user can stop the demonstration earlier.

## Reproduce offline

`prepare_packaging_demo.py` creates a fresh model/manifest and a shorter preview
model without contacting a Historian. The standalone DemoPreview tool reads the
full model and generates at most its first 72 hours/100000 candidate slots/16 MiB.
Its output can be saved as `preview-72h.json`; the independent
`check_packaging_preview.py <folder>` checks that output using the manifest.
The model builder and checker are also part of `verify_offline.py` in CI.

The conversation's time scrubber uses the actual offline preview. Its script and
embedded data passed static checks; interactive browser rendering was blocked by
the browser's local-file URL policy and was not verified.

## Current verification checkpoint

Release build succeeded with zero warnings; 562 .NET tests and 22 Python tests
passed locally. Existing host/process/SIGINT verification passed. Offline schema,
100 gate-oracle scenarios and the 72-hour packaging checker passed. Code checkpoint `a036dcb` passed Linux, macOS and Windows
[CI run 35568320181](https://github.com/jbknepp98/industrial-data-sim/actions/runs/35568320181),
including schema/oracle, host/shutdown and capacity smoke checks. No user acceptance has
been recorded and no submitted timestamps have been replayed.


## Remaining Windows verification concern

Reviewing the previous docs-only checkpoint `f083814` revealed two failures in
[its Windows CI run](https://github.com/jbknepp98/industrial-data-sim/actions/runs/35545016238):
`KilledCancellationProcessPreservesAtomicOutcome` could not acquire the owner
file after the child exited, and `KilledProcessRecoversWithoutRunningCleanup`
encountered Access Denied removing `state.db-shm` during fixture cleanup. The
existing bounded sharing-violation cleanup retry does not settle these cases.
Their cause remains unconfirmed; do not infer data corruption or claim they are
fixed from a later green run. This is a separate Windows recovery-test reliability
follow-up. The live demonstration runs on macOS and passed its explicit restart.
