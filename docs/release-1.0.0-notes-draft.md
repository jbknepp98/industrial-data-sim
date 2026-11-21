# industrial-data-sim 1.0.0: release notes (DRAFT)

Draft prepared November 21, 2026, after the Phase 5 run. This is not release
approval. The owner pattern review (V19) and the Phase 6 checks below must pass
first, and the owner approves the tag.

## What it is

An agent-operated .NET 10 command-line application that generates synthetic
industrial time-value-quality data from declarative JSON models and publishes it
to Timebase Historian. It backfills history, then continues live at wall-clock
pace in the same session. Durable local SQLite state lets it stop, restart, and
extend without resetting pattern clocks or replaying data.

## Highlights

- **Signals:**
  - numeric, Boolean, and string outputs;
  - constant, bounded ramp, staircase, and seeded random holds;
  - sequences and Boolean-triggered branches;
  - gates that pause or continue pattern time.
- **Manufacturing models:**
  - SKU routing to mutually exclusive cells, with readiness interlocks;
  - state machines;
  - seeded noise, freeze faults, and quality faults;
  - elapsed-time and production-batch totalizers;
  - target-bounded completion.
- **Operations:**
  - admission with read-only preflight, then status, pause/resume, and cancellation;
  - guarded horizon extension and graceful host stop/restart;
  - bounded archival of completed history;
  - guarded disaster restore to fresh tag names.
- **Delivery safety:**
  - per-tag timestamps strictly increase;
  - an ambiguous publish becomes Uncertain and is never automatically replayed;
  - payloads are hash-checked.
- **Historian 2.0.0 support:** the publisher parses the 2.0.0 write receipt.
  Server-reported drops are logged (`delivery.points_dropped`) and the batch stays
  Published; it is never resent.
- **Packaging:** self-contained ZIPs for win-x64, linux-x64, and osx-arm64, with
  bundled .NET and SQLite, checksums, and dependency notices.

## Qualification summary

The candidate `1.0.0-rc.2` (`e866609`) was run against Timebase Historian/Pulse
2.0.0 on macOS ARM64 with:
- four concurrent sessions and 32 tags;
- a 72-hour backfill, then 3 h 22 min live;
- 1.09 M points stored.

Results:
- No session Failed or went Uncertain, and nothing was dropped or resent.
- Control p95 was 909 ms and live lag p95 was 1.57 s.
- The restart and horizon-extension drills passed.
- Read-back matched the preview with 0 mismatches.

Details are in `phase-5-acceptance-plan.md` and the acceptance matrix.

## Compatibility

- **Qualified live:** Historian/Pulse 2.0.0 (digests in `compatibility.md`), on macOS
  26.2 ARM64.
- **Package lifecycle checks only:** linux-x64 and win-x64 passed on CI runners,
  but have no live Historian run.
- **Not compatible:** `1.0.0-rc.1` and earlier builds, against Historian 2.0.0,
  because they treat the write receipt as an unexpected response.

## Known limitations

- No exactly-once delivery or per-point acceptance guarantee. Published progress
  and arrival indicators are not per-point receipts.
- The live soak was three hours (owner scope decision, October 26, 2026). The
  seven-day live horizon is within guards but was not exercised end to end.
- Precompiled manufacturing models are capped at 250,000 node-clock evaluations.
  Target-bounded models therefore use a short backfill, while continuing models
  use windowed execution.
- One host owns each state database. There is no distributed ownership.
  Multiple databases or external writers must use disjoint tags.
- Disaster restore requires fresh tag names. Same-tag continuation needs the
  original latest database.
- Private-CA trust checks hostname, chain, validity, and server-auth usage, but
  not revocation.
- Startup templates were parsed and lifecycles tested; no OS service was registered
  or boot-tested on a deployment machine.
- Safety ceilings, not capacity guarantees:
  - 32 production tags per session;
  - 100 unarchived sessions;
  - 1,000 horizon revisions;
  - a 128 MiB archive export limit.
- Not included: graphical editing, cross-session live inputs, arbitrary
  executable model code, and archive replay as a recovery feature.

## Phase 6 checklist (before tagging)

1. **Owner pattern review (V19)** of the run's trend page. Record accepted
   observations separately from delivery progress.
2. **Final code, docs, and error audit (V20).** Handoff, installation steps, and
   examples match the commands that actually ship. Historical claims are dated.
3. **Release build decision.** `package_release.py` currently refuses a bare
   `1.0.0`. Building `1.0.0` from `e866609` changes only the version string.
   The owner decides whether that is acceptable as-is, or whether the
   package-lifecycle and smoke checks must be repeated on the final artifact.
   The smoke run is recommended either way.
4. **Exact-artifact verification (V21):**
   - clean three-platform CI;
   - `verify_package.py --require-clean`;
   - dependency audit;
   - checksums;
   - installed-artifact smoke.
5. **Owner decisions:**
   - distribution license (none selected yet);
   - signing and notarization expectations;
   - whether an OS service registration test is required for 1.0.
6. **Approval and immutable tag (V22):** `v1.0.0` tag, matching artifacts, these
   notes finalized, and an evidence index.
