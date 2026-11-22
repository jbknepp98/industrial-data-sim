# industrial-data-sim 2.0.0-rc.2: release candidate

Candidate, not a release. Approved by the owner on November 22, 2026: 2.0.0-rc.1 plus
the changes below. The tag `v2.0.0-rc.2` goes on the version-only commit whose CI run
passes every job.


- **Each plant approved on the page gets its own Historian Dataset** (owner decision,
  November 22, 2026). Approve creates a Dataset named exactly like the plant, with a
  365-day purge age, and the plant's tags go only there. A name that a Dataset
  already has is refused (`approve.dataset_exists`), so the page never writes to a
  Dataset it did not create. The Remove dialog has **Also delete its Dataset**, off by
  default; it deletes only a Dataset the page created. `TIMEBASE_DATASET` is now
  optional: only plants approved with rc.1 still use it. The CLI is unchanged.
- The page's files are served with `Cache-Control: no-cache`, so a browser uses a new
  image's page and script at once instead of a cached copy from the previous version.
- Checked live on a dev build (acceptance P22): own Dataset with a 365-day purge age,
  0 mismatches, nothing written to `Test`, and the Dataset deleted on Remove when asked.

# industrial-data-sim 2.0.0-rc.1: release candidate

Candidate, not a release. Approved by the owner on November 22, 2026 as the
first 2.0.0 candidate: the plant simulation (Postgres rows and Historian tags
from one clock, snapshots, Quality, Shipping, certification, batch units and
golden batch drift, `EngineVersion` 4) and the web interview container
`flow-data-simulator-web` (Claude through Pulse Gage, approval in the page,
in-container hosting). The tag `v2.0.0-rc.1` goes on the version-only commit
whose CI run passes every job, including the new `web-image` job.

- Deployment: [deployment/web/README.md](../deployment/web/README.md).
- What it does and the live acceptance of November 22, 2026:
  [plant-simulation.md](plant-simulation.md#packaging-and-live-acceptance-phase-8).
- Scope and gates: [release-2.0.0-specification.md](release-2.0.0-specification.md)
  and [release-2.0.0-acceptance.md](release-2.0.0-acceptance.md). The fresh install
  from the CI-built arm64 image passed on November 22; the multi-day soak of the two
  audience plants is still open. The 1.x session host is unchanged.
- Platforms and the Timebase stack used: [compatibility.md](compatibility.md#release-200-rc1-november-22-2026).
- After the tag, `b5dd016` made CI tag the web image with its version as well as its
  commit, so a loaded artifact starts with the compose file unchanged (CI and README
  only).

# industrial-data-sim 1.0.1: release notes

Released November 21, 2026, with the owner's approval. This is the 1.0.0 program (no runtime code change)
plus a native **linux-arm64** package, so Docker on ARM64 hosts runs without x64
emulation. Docker is the only supported deployment. Tag `v1.0.1` is on `192aaab`,
built by [CI run 44912679076](https://github.com/jbknepp98/industrial-data-sim/actions/runs/44912679076)
(all seven jobs passed).

| Artifact | SHA-256 |
| --- | --- |
| `industrial-data-sim-1.0.1-linux-arm64.zip` | `0874eeb15bfe8136d57e5b5c15148a8eca945ed16e7bb397ba677a7bbbac313e` |
| `industrial-data-sim-1.0.1-linux-x64.zip` | `ff50ccc4cc0dd6b67f1963ae41c28853c321bb1bacf6a71e30ac151cb212c69e` |
| `industrial-data-sim-1.0.1-osx-arm64.zip` | `c35787a0cca93fe6e40b8fdd466486e2a337b6ec7ebcbd7c3955e95a975d2d6c` |
| `industrial-data-sim-1.0.1-win-x64.zip` | `623882e7fef7c008eec1565f2f36e14e13c466040eb5c42ae9fb2d02eb5a1e2b` |

Checks run on the linux-arm64 package:

- package verification in a native ARM64 container;
- dependency audit: clean;
- full Docker smoke run, natively: catch-up 21 s, status p95 416 ms, lag p95
  1.27 s. The extension, restart, pause/resume and completion drills passed,
  with no Failed or Uncertain sessions, drops or resends;
- `docker stop` with SIGINT during backfill: exit 130, and the restart
  continued forward.

The long Docker soak will be run by the owner. Everything below applies
unchanged from 1.0.0.

# industrial-data-sim 1.0.0: release notes

Released November 21, 2026, with the owner's approval. Tag `v1.0.0` is on commit
`cdf0681`, the commit that built the artifacts below. It is a version-only
rebuild of the accepted candidate `1.0.0-rc.2` (`e866609`): `src`, `tools` and
`tests` are unchanged. Built by [CI run 44908019551](https://github.com/jbknepp98/industrial-data-sim/actions/runs/44908019551).

| Artifact | SHA-256 |
| --- | --- |
| `industrial-data-sim-1.0.0-osx-arm64.zip` | `775612dcf62c2479568abe564d1156b7b8f1a3c9eec0b58bd92780cf126c2b73` |
| `industrial-data-sim-1.0.0-linux-x64.zip` | `704e80ec081640d9828714021627ce34a0bd12a201cca6a84cecbdd7977aaa8b` |
| `industrial-data-sim-1.0.0-win-x64.zip` | `18fdaac30eb2932bde8d53a04406d69c9b54e7955ecffe50ba42377c4e431988` |

Intended use: the owner and their team only, locally, deployed in Docker. No
distribution license is granted (owner decision, November 21, 2026).

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

- **Historian/Pulse 2.0.0** (digests in `compatibility.md`): qualified live.
  - The three-hour acceptance ran with rc.2 on macOS 26.2 ARM64.
  - The 1.0.0 smoke runs passed on both the macOS package and the `linux-x64`
    package in a Docker container (x64 emulation on Apple Silicon).
- **win-x64**: package lifecycle checks on CI only.
- **Not compatible:** `1.0.0-rc.1` and earlier builds, against Historian 2.0.0.

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
- Docker is the supported deployment, using a locally built image (see
  installation.md). The host must be stopped with SIGINT, because it does not
  handle SIGTERM gracefully. The image needs ICU. 1.0.1 adds the native
  `linux-arm64` package.
- OS service templates (launchd, systemd, Task Scheduler) ship, but none was
  registered or boot-tested. Not needed for the Docker deployment.
- Unsigned and not notarized (internal use).
- Safety ceilings, not capacity guarantees:
  - 32 production tags per session;
  - 100 unarchived sessions;
  - 1,000 horizon revisions;
  - a 128 MiB archive export limit.
- Not included: graphical editing, cross-session live inputs, arbitrary
  executable model code, and archive replay as a recovery feature.

## Phase 6 record

1. **Owner pattern review (V19):** the owner reviewed the trend page and
   approved the release on November 21, 2026.
2. **Docs and diagnostics (V20):**
   - installation steps now cover 1.0.0 and Docker;
   - handoff and compatibility are updated;
   - historical claims stay dated.
3. **Release build:** the owner approved a version-only rebuild, so
   `package_release.py` now accepts exactly `1.0.0`.
4. **Exact-artifact checks (V21):**
   - CI passed on all six jobs;
   - `verify_package.py --require-clean` passed on osx-arm64;
   - the dependency audit was clean.
5. **1.0.0 smoke runs:**
   - **macOS:** catch-up 22 s, status p95 414 ms, lag p95 1.42 s.
   - **Docker linux-x64:** catch-up 28 s, status p95 966 ms, lag p95 1.30 s.
   - Both passed the extension, restart, pause/resume and completion drills,
     with no Failed or Uncertain sessions, drops or resends.
   - The Docker SIGINT stop and restart was verified separately.
6. **Owner decisions:**
   - no license; internal use only;
   - signing not required;
   - Docker deployment, so no OS service test.
7. **Approval and tag (V22):** `v1.0.0` on `cdf0681`.
