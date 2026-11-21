# Phase 5 acceptance plan (1.0.0-rc.2)

Prepared November 20, 2026. This plan is reviewed before any acceptance write.
Gate IDs refer to the [acceptance matrix](release-1.0.0-acceptance.md). The live
duration is three hours after a 72-hour backfill (owner scope decision, October 26).

## Frozen candidate

| Item | Value |
| --- | --- |
| Version | `1.0.0-rc.2` |
| Source commit | `e866609` (clean CI build) |
| CI run | [44842881320](https://github.com/jbknepp98/industrial-data-sim/actions/runs/44842881320), all six jobs passed |
| Executed artifact | `industrial-data-sim-1.0.0-rc.2-osx-arm64.zip`, SHA-256 `a0b1f23fb7d2c2bbf38fef00f7fe2846a47698b597529ef05cd8b4a4d9435648` |
| Local verification | `verify_package.py --require-clean` passed; dependency audit clean |
| Schemas | SQLite 10, archive format 3 |
| Target stack | Timebase Historian/Pulse 2.0.0 ([compatibility](compatibility.md)) |

Superseded: `1.0.0-rc.1` (`a66d611`) could not parse the Historian 2.0.0 write
receipt; its smoke run's first batch went Uncertain and was not resent.

Later commits that only add tooling or documentation outside the package
allowlist do not change this candidate. Any change to packaged code or files
requires a new rc number and repeats the affected acceptance.

## Workload

`scripts/prepare_rc_acceptance.py` writes four models and a manifest (seeds, tag
names, model hashes) to a new ignored folder. All sessions use the `Test` Dataset
with disjoint tags under one fresh namespace `RC1-<anchor>`; nothing else in the
deployment is read for writing or modified.

| Session | Requirement covered | Backfill | Content |
| --- | --- | --- | --- |
| Utilities | Simple signals | 72 h | Random holds, bounded ramp, hourly day/night staircase, randomized-dwell staircase, Boolean and string constants |
| PackagingLine | Gated sequences | 72 h | Eight-hour shifts with 30-minute changeover; pause and continue gates, gated sequence, branch switch |
| ProcessCell | Two-cell SKU routing, both totalizer modes, noise/faults | 72 h | Windowed manufacturing; SKU-A to cell A, SKU-B to cell B; freeze and bad-quality faults in backfill and live |
| BatchReactor | Target-bounded production | 6 h | Precompiled manufacturing, one-second clock; stops at 6,500 kg about 90 minutes into live |

The target model's short backfill is deliberate: precompiled execution is capped
at 250,000 node-clock evaluations, and the specification requires 72 hours only
for continuing models.

## Offline preview

`scripts/preview_rc_acceptance.py` previews each model at one-minute sampling
through the full live horizon (exact subsample for manufacturing; pattern shape
for generators) and independently checks per-tag ordering, exclusive SKU
routing, gate suppression, monotonic totalizers and target completion inside
the initial live horizon. Trial workload (anchor 2026-11-21T15:00Z): passed,
29 SKU changes, 19 shift edges, 5 bad-quality samples, target at live +90 min.
All four models also validated with the rc.1 package; the run admits them with rc.2. Regenerate and re-preview
with the real anchor before admission.

## Run procedure (requires owner approval of anchor and namespace)

1. Regenerate the workload with the approved anchor; preview must pass.
2. Admit four sessions with the packaged CLI (`production start`, read-only
   preflight) into a new private state database. Record admission results.
3. Start `production-host run` from the package. Backfill publishes until each
   continuing session reaches real time, then continues live without re-admission.
4. Measure throughout (V02, V04–V06, V17): per-session Published progress, queue
   points/bytes, process memory, database/disk growth, status/pause latency, and
   newest published timestamp lag once live. Record arrival/change observations
   separately. Targets: p95 control ≤ 5 s, p95 live lag ≤ 10 s.
5. Restart drill (V18): about one hour into live, record cursors/revisions,
   stop the host gracefully, wait for exit, restart from the same database, and
   confirm forward-only timestamps and unchanged identity.
6. Extension drill (V18): extend each continuing session so at least three hours
   of steady live operation follow backfill catch-up; record revision and new end.
7. Let BatchReactor complete at its target; continuing sessions finish at the
   extended end. Collect observations, then the owner reviews the trends (V19).

Stop conditions: any Failed or Uncertain session, a timestamp-order violation,
or a security/diagnostic problem. Preserve state; never replay or resume
Uncertain work; a repair means a new rc and a rerun of affected steps.

## Results: smoke and main run (November 21, 2026)

Candidate as frozen above, on the local Mac (macOS 26.2 ARM64) against
Historian/Pulse 2.0.0, Dataset `Test` (purge age 7 days, size 0, late-data
defaults). The driver is `scripts/run_rc_acceptance.py`; the raw state, logs and
samples are kept in ignored local storage.

Smoke runs, short horizon (`SMOKE-*` namespaces):
- **rc.1**: Uncertain on its first publish, because it could not parse the 2.0.0
  write receipt. Nothing was resent, and the candidate was superseded.
- **rc.2, first smoke run**: the driver stopped on its own parsing bug (a nested
  horizon reply) and stopped the host cleanly. No session failed. The driver was
  fixed in `8e7be3f`, a tooling-only change outside the package.
- **rc.2, second smoke run**: everything passed. Catch-up took 21 s. The
  extension, restart, and pause/resume drills all passed. Status p95 was 375 ms
  and live lag p95 was 1.24 s.

Main run: namespace `RC1-20261121T0041`, anchor 2026-11-21T00:41Z, seed 20261120.
The workload was regenerated for that anchor and the preview passed.

| Gate | Observation | Result |
| --- | --- | --- |
| V02 | Four sessions and 32 tags all progressed. 1,090,408 points were stored, read back after the run: Utilities 250,721; PackagingLine 256,188; ProcessCell 531,444; BatchReactor 52,055. | Passed |
| V03/V17 | The 72-hour backfill reached real time without re-admission. Catch-up times after the anchor: BatchReactor (6 h backfill) 130 s, PackagingLine 1,246 s, Utilities and ProcessCell 1,316 s. Live operation continued to 04:03Z, 3 h 22 min after the anchor and 3 h after catch-up. | Passed |
| V04 | The maximum queued points in any sample was 0. Every session drained at completion. | Passed |
| V05 | Status p95 was 909 ms over the whole run (max 2.93 s, during catch-up) and 826 ms after catch-up (max 2.04 s), against a target of 5 s or less. Live lag p95 was 1.57 s (max 5.26 s; after catch-up 1.42 s, max 4.73 s; 2,254 samples), against a target of 10 s or less. Pause took 260 ms and resume 257 ms. | Passed |
| V06 | Process memory was 50–129 MB, with the peak at startup and catch-up; after catch-up it held steady between 72 and 86 MB. The state database grew linearly to 45 MB. | Recorded |
| V08 | No session was Failed or Uncertain. The runtime logged no warnings or errors, so no `delivery.points_dropped` was reported. No batch was resent. | Passed |
| V16 | rc.2 publishes to 2.0.0, accepts its write receipt, and the 72-hour backfill was stored without drops. | Passed for this stack |
| V18 | Two horizon extensions per continuing session (revisions 1 and 2). In the restart drill at 02:03Z, the graceful stop took 0.6 s and the restart from the same database kept the same revision with forward-only cursors in all four sessions. | Passed |
| V17 | BatchReactor stopped on target: `ProducedKg` reached exactly 6,500 at live +90 min (02:11Z) and the session completed. The continuing sessions completed at the extended end. | Passed |
| V19 | Read-only Historian read-back compared 5,504 preview minutes across 168 tag-windows. There are ten event windows: backfill start, the 30 h and 60 h faults, a shift changeover, the reactor backfill, the anchor, the live fault at 72 h 45 m, the restart and reactor end, the pause, and the run end. It found 0 value or quality mismatches and 0 ordering violations, and analog values were identical. Arrival indicators changed for every session. The full trends for owner review are on the run's trend page. | Awaiting owner review |

Read-back confirmed 5 bad-quality samples and 19 `LineReady` transitions, both
matching the preview. Published progress and read-back are evidence of what
was stored, not per-point receipts; absence never authorizes replay.
