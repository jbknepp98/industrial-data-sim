# Compatibility record

## Release 1.0.1 (November 21, 2026)

The supported deployment is Docker; the owner never runs it natively on macOS.

| Artifact RID | Evidence |
| --- | --- |
| linux-arm64 | Built and checked on the CI `ubuntu-24.04-arm` runner. `verify_package.py --require-clean` passed in a native ARM64 Debian container. Full smoke passed natively in Docker on Apple Silicon against 2.0.0, and the SIGINT stop and restart were verified. **Primary target.** |
| linux-x64, osx-arm64, win-x64 | Same source as 1.0.0, rebuilt; CI package checks passed. See 1.0.0 below for live evidence. |

## Release 1.0.0 (November 21, 2026)

| Artifact RID | Evidence |
| --- | --- |
| osx-arm64 | CI package checks passed. Locally verified on macOS 26.2 ARM64 against Historian/Pulse 2.0.0: the rc.2 three-hour acceptance and the 1.0.0 smoke run both passed. |
| linux-x64 | CI package checks passed. 1.0.0 smoke passed in a Docker container (`python:3.12-slim` with ICU) under x64 emulation on Apple Silicon, on the Timebase stack network, against 2.0.0. The SIGINT stop and restart were also verified. |
| win-x64 | CI package checks only. |

Container prerequisites, stop signal and network naming are in installation.md.
A container image is not shipped, and long container runs are unqualified.

## Phase 4 baseline

Phase 4 candidate: 0.4.0-phase4.1. No general OS or Timebase version range is
claimed. ZIP artifacts bundle .NET and SQLite native components for their RID.

| Artifact RID | Qualification target | Evidence |
| --- | --- | --- |
| linux-x64 | GitHub ubuntu-24.04 x64 | Extracted-package CI passed (`afb669d`) |
| osx-arm64 | GitHub macos-15 ARM64; local macOS 26.2 ARM64 | Extracted-package CI and downloaded CI artifact on local Mac passed (`afb669d`) |
| win-x64 | GitHub windows-2022 x64 | Extracted-package CI passed (`afb669d`) |

Other architectures, musl/Alpine, Windows ARM64, macOS Intel, simulator containers
other than the Debian-based smoke run recorded above, unattended reboot and service-manager registration are unqualified.
Hosted runners include native OS prerequisites; this is a clean extraction
without an SDK/runtime dependency, not proof on every bare OS installation.

## Local Timebase deployment, November 20, 2026 (Phase 5 target)

The local stack was upgraded through 2.0-gc4 and 2.0-gc6 to the 2.0.0 release
after the October baseline. Phase 5 acceptance targets this deployment. Read-only
Docker identity inspection:

| Component | Image tag | Immutable image/repository digest | Architecture |
| --- | --- | --- | --- |
| Historian | timebase/historian:2.0.0 | sha256:a16b8e33604312c506559ef21669b935e19b5645b5cac8743fce6cf8066887f7 | linux/arm64 |
| Pulse | timebase/pulse:2.0.0 | sha256:e96725f3af8a4b7d55d5a1438b2da583de146d4620b85abd8f2b0bf775297c86 | linux/arm64 |

The Pulse root CA was rotated on October 21, 2026; clients need the new root
in their PEM trust bundle. The earlier `Test` Dataset no longer existed after the
upgrade. A new `Test` Dataset was created for simulator acceptance only (purge
age seven days, purge size zero, late-data settings left at server defaults).
Other Datasets on the deployment belong to other work and must not be written,
reconfigured or deleted by simulator testing.

Read-only Python probe: passed with verified TLS, token acquisition and expected
Test settings. No values were written.

C# adapter preflight (`production start`, development build of `05af1db`, scratch
state database, three fresh `CompatSmoke-20261120.*` tags): admitted after
authentication, existence, settings, tag inventory and current-value reads. It
parsed `lateToleranceMs` 0 and `lateAgeDays` 30. `production run` was not invoked,
so no values were written; the session is left unpublished in ignored local state.
This is a compatibility smoke check, not exact-candidate evidence.

Smoke run of 1.0.0-rc.1 (November 21, 2026, `SMOKE-20261121T0013.*` tags in
Test): the first publish returned HTTP 200 with the new 2.0.0 write receipt body,
which rc.1 treated as an unexpected response, so that session became Uncertain
and the run stopped with state preserved. Nothing was resent. 1.0.0-rc.2 accepts
the receipt; see timebase-api-findings.md. rc.1 is not compatible with 2.0.0.

Historian 2.0.0 Dataset settings expose late-data tolerance (`ldt`, ms; server
default 0) and late-data age (`lda`, days; server default 30) plus read-only size,
tag-count and age fields. The adapter already reads `ldt`/`lda` as optional and
ignores unrecognized members, so the new fields do not change preflight. The
simulator never sends an older timestamp than one already sent for the same tag
and depends on neither setting; the 72-hour backfill is far inside the 30-day
default. This is a reasoned expectation, not yet exact-candidate live evidence.

## Local Timebase deployment, October 19, 2026 (superseded)

User screenshot and read-only Docker identity inspection agree on:

| Component | Image tag | Immutable image/repository digest | Architecture |
| --- | --- | --- | --- |
| Historian | timebase/historian:2.0-gc1 | sha256:ee76111001fbad484ddee67f934350bd6e70917aeefc65db6e01ea39bdc9d483 | linux/arm64 |
| Pulse | timebase/pulse:2.0-gc1 | sha256:e3bf9684595809119e18734b34bb83f4f9cb6d76c65f298bf5b117e697b160b1 | linux/arm64 |

These are deployment identities, not test results. Read-only authentication and
Dataset shape probe: passed with verified TLS, token acquisition and expected
Test settings (purge age seven days, size zero). No values were written.
This Python read-only probe is independent of the packaged C# adapter.
Retention means early backfill can age out during the live soak; collect
transition evidence promptly and do not infer missing-point delivery failure. Published TVQ/arrival behavior and seven-day
continuity on the exact packaged candidate remain Phase 5 acceptance. Earlier
live evidence does not substitute for an exact-candidate test. Do not restart,
upgrade or reconfigure these containers merely to collect compatibility evidence.

Candidate artifacts and all three package checks: [CI run](https://github.com/jbknepp98/industrial-data-sim/actions/runs/39325253000).
Runner CPU labels follow [GitHub's published matrix](https://docs.github.com/en/actions/reference/runners/github-hosted-runners); the actual native executables were exercised on each listed runner.
