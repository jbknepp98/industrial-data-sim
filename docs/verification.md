# Reproducing verification

Prerequisites: stable .NET 10 SDK, Python 3.9 or later with venv, and package
access for the initial restore/install. Run from the repository root:

```sh
dotnet test IndustrialDataSim.slnx --configuration Release
python3 scripts/verify_host.py
python3 -m venv .tools/verification
.tools/verification/bin/python -m pip install -r scripts/requirements.txt
.tools/verification/bin/python scripts/verify_offline.py
.tools/verification/bin/python -m unittest discover -s scripts -p 'test_*.py'
```

If using the optional local SDK, pass `--dotnet "$PWD/.tools/dotnet/dotnet"`
to `verify_offline.py`, and use that executable for the build/test command.
The verifier locates repository files relative to itself, not the working directory.

The .NET suite covers generator semantics, parsing, diagnostics, CLI output,
SQLite ownership and queues, deterministic restart, and fake delivery. A test-only
child executable is killed at six durable boundaries; no cleanup handlers run.
These tests need local process-launch permissions and writable temporary storage.
They use synthetic configurations and never contact Historian.
The independent Python check validates all eight simulation examples and the
header/subset schemas, rejects six invalid gate shapes through both schema and
CLI, checks repeatability, and exercises 100 seeded gate timelines against an
independent millisecond counter (3722 emitted samples). These checks do not
contact a server. Schema validation is structural; the runtime also enforces
cross-field constraints that JSON Schema does not express.

## Saved live-test evidence

The read-only verifier accepts a generated preview and a saved full Historian
read-back. Supply the exact preview range, for example:

```sh
python3 scripts/verify_readback.py state/example/preview.json state/example/full-readback.json \
  --start 2026-09-17T00:00:00Z --end 2026-09-18T00:00:00Z
```

It checks tag sets, retained order, timestamps, stored types, values, quality,
and required value/quality transitions. Numeric comparisons use relative tolerance
1e-12 and absolute tolerance 1e-9; Boolean 0/1 representation is accepted.
Read-back boundary points outside the half-open range are ignored. This helper
uses Python datetime precision (microseconds); do not use it to certify distinct
100 ns timestamps. Current saved live tests use whole-minute samples.
Omitted repeated samples are not claimed individually delivered. Synthetic
unit tests cover the verifier without requiring private evidence files.

Credentials, certificates, payload artifacts, and read-backs remain ignored.
Historical local preparation/write scripts were one-off probes and are not
supported repository commands. Their read-back comparison is now reproducible
using this sanitized helper. No live-write command is enabled by these checks.
Future live-write tooling must be explicitly invoked, use fresh unique tags,
persist submission intent, and refuse replay after an interrupted run. It must
follow the [recovery policy](delivery-recovery.md).

## Host process verification

After the Release build, `python3 scripts/verify_host.py --dotnet <sdk-executable>`
starts the CLI host and clients as separate processes, checks exclusive ownership,
live admission/pause/resume/status, disjoint peer completion, retained pause across
restart and graceful control stop. Unix checks SIGINT; Windows now checks CTRL_BREAK in a child process group sharing
a test console (allocated when absent). It does not substitute a control stop for
a console-signal test. It uses temporary simulation-only state
and deletes it on completion. It requires local process and pipe access. No mutation
is retried; only read-only host readiness probes may repeat. The .NET suite also
checks malformed control frames, inventory limits, lifecycle errors, and corrupted
saved positions before submission and after acceptance.

## Continuous integration

`.github/workflows/verify.yml` runs on main pushes, pull requests, and manual
dispatch. The matrix uses Ubuntu 24.04, macOS 15, and Windows Server 2022 with
.NET 10 and Python 3.12. Official setup actions are pinned to commit hashes;
the workflow has read-only repository permissions and does not persist Git
credentials. It runs the Release build, full .NET suite, Python unit tests,
schema/oracle checks, host process smoke, and a synthetic capacity smoke case.
It also runs the five-second live-control capacity probe against temporary state.
The short-deadline live-host .NET test collection runs separately from parallel
bulk SQLite/crash tests. A Windows run exposed a three-second pipe-connect timeout
under the combined test load; isolating the collection avoids conflating unrelated
test contention with host protocol checks. Production deadlines are unchanged,
and the separate-process load probe still exercises controls during generation.
No Historian configuration, secrets, or network writes are used by these checks.
Dependency downloads still require network access. A green workflow is platform
test evidence, not proof of production Historian compatibility.

To reproduce the capacity checks, build the entire solution first (dotnet test
does not build the standalone probe), then follow [capacity and retention](capacity-and-retention.md).

The first full hosted matrix passed on September 19, 2026 for `ee058f4`: [run 35452466245](https://github.com/jbknepp98/industrial-data-sim/actions/runs/35452466245). Each platform passed all 520 .NET tests, eight Python tests, the schema/oracle checks, host smoke, and the capacity smoke. This result does not cover Windows console signals or live Historian delivery.

Latest code checkpoint `141a031` passed the complete Ubuntu/macOS/Windows matrix: [run 35453423029](https://github.com/jbknepp98/industrial-data-sim/actions/runs/35453423029). Each platform passed 521 .NET tests, 12 Python tests, schema/oracle checks, process verification, the loaded-control smoke, and the original capacity smoke. The preceding two Windows runs had the timed-host connect failure; the isolated collection passed here. This does not establish latency guarantees under arbitrary host or machine load.

The September 20 diagnostic checkpoint `3fdc1a5` passed Linux, macOS and Windows: [CI run 35524198322](https://github.com/jbknepp98/industrial-data-sim/actions/runs/35524198322). Each platform passed 522 .NET tests, 14 Python tests, the schema/oracle checks, standard host process verification, diagnostic load smoke and capacity smoke. Native profiling is macOS-only and was exercised locally; neither armed window triggered a stack sample.

The diagnostic review repairs on September 20 passed locally: 522 .NET tests and
20 Python tests, including launch-delay accounting, timeout cleanup, safe partial
evidence and sampler finalization. Real synthetic host checks verified successful
log discovery and event retention after an injected command timeout. These checks
do not reproduce or resolve the intermittent control delay. The corrected
[diagnosis](control-latency-diagnosis-2026-09-20.md) retracts earlier empty-log
inferences caused by the collector's directory error.

The operational/archival/production increment passed locally with 545 .NET tests
and 20 Python tests, schema comparisons, 100 independent gate scenarios (3722
samples), host process checks, a fifteen-minute mixed-load run and an explicit
two-session Test Dataset smoke. The .NET suite includes real local TLS trust and
hostname rejection tests. Hosted results are recorded in the
[current increment report](phases-1-3-verification-2026-09-20.md).

Checkpoint `722868f` passed all three hosted platforms with 545 .NET tests and
20 Python tests, including the real TLS fixtures and Windows CTRL_BREAK process
verification: [run 35544324314](https://github.com/jbknepp98/industrial-data-sim/actions/runs/35544324314).
The first Windows attempt exposed the synthetic certificate key-container issue
described in the increment report; production TLS verification was not weakened.

Final code checkpoint `370eb8f` passed the full hosted matrix, including archive
directory persistence and bounded Windows test cleanup: [run 35544826514](https://github.com/jbknepp98/industrial-data-sim/actions/runs/35544826514).
All three platforms passed 545 .NET and 20 Python tests plus the schema, host,
diagnostic-load and capacity checks. Remaining operational limits are recorded
in the current increment report; a green matrix does not resolve the intermittent
control latency or establish per-point Historian acceptance.
