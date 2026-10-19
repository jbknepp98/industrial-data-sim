# Compatibility record

Phase 4 candidate: 0.4.0-phase4.1. No general OS or Timebase version range is
claimed. ZIP artifacts bundle .NET and SQLite native components for their RID.

| Artifact RID | Qualification target | Evidence |
| --- | --- | --- |
| linux-x64 | GitHub ubuntu-24.04 x64 | Extracted-package CI pending |
| osx-arm64 | GitHub macos-15 ARM64; local macOS 26.2 ARM64 | Extracted-package CI/local check pending |
| win-x64 | GitHub windows-2022 x64 | Extracted-package CI pending |

Other architectures, musl/Alpine, Windows ARM64, macOS Intel, containers for the
simulator, unattended reboot and service-manager registration are unqualified.
Hosted runners include native OS prerequisites; this is a clean extraction
without an SDK/runtime dependency, not proof on every bare OS installation.

## Local Timebase deployment, October 19, 2026

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
