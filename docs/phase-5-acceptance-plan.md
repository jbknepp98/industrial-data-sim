# Phase 5 acceptance plan (1.0.0-rc.1)

Prepared November 20, 2026. This plan is reviewed before any acceptance write.
Gate IDs refer to the [acceptance matrix](release-1.0.0-acceptance.md). The live
duration is three hours after a 72-hour backfill (owner scope decision, October 26).

## Frozen candidate

| Item | Value |
| --- | --- |
| Version | `1.0.0-rc.1` |
| Source commit | `a66d611` (clean CI build) |
| CI run | [44839471116](https://github.com/jbknepp98/industrial-data-sim/actions/runs/44839471116), all six jobs passed |
| Executed artifact | `industrial-data-sim-1.0.0-rc.1-osx-arm64.zip`, SHA-256 `66b66861c663d61659640ab99ce3806bc784fc3acf51430b06f72eb425efed35` |
| Local verification | `verify_package.py --require-clean` passed; dependency audit clean |
| Schemas | SQLite 10, archive format 3 |
| Target stack | Timebase Historian/Pulse 2.0.0 ([compatibility](compatibility.md)) |

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
All four models also validate with the packaged binary. Regenerate and re-preview
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
