# Reproducing verification

Prerequisites: stable .NET 10 SDK, Python 3.9 or later with venv, and package
access for the initial restore/install. Run from the repository root:

```sh
dotnet test IndustrialDataSim.slnx --configuration Release
python3 -m venv .tools/verification
.tools/verification/bin/python -m pip install -r scripts/requirements.txt
.tools/verification/bin/python scripts/verify_offline.py
.tools/verification/bin/python -m unittest discover -s scripts -p 'test_*.py'
```

If using the optional local SDK, pass `--dotnet "$PWD/.tools/dotnet/dotnet"`
to `verify_offline.py`, and use that executable for the build/test command.
The verifier locates repository files relative to itself, not the working directory.

The .NET suite covers generator semantics, parsing, diagnostics, and CLI output.
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
