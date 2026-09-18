# Industrial Data Simulator

A planned industrial data simulation tool that writes Timestamp Value Quality
(TVQ) points to Timebase Historian Datasets.

## Status

Initial API exploration is complete. Authentication, certificate verification,
automatic tag creation, and batched writes were exercised against a development
installation. The current implementation provides a .NET solution and
offline Dataset-name, session-header, and simulation-model validation commands.
A bounded dry-run generates deterministic constant, ramp, staircase, and random-integer-hold TVQ data, including finite sequences and local Boolean triggers. A library runtime now persists concurrent sessions, tag reservations, checkpoints,
and bounded TVQ queues in SQLite. Delivery and crash recovery are exercised against
a sealed in-memory fake Historian. Production Historian delivery and a hosted
background worker remain unimplemented.

See [API findings](docs/timebase-api-findings.md) for payloads, observed behavior,
and unresolved questions.

See the [Phase 1 implementation plan](docs/phase-1-plan.md) for the proposed
session engine, conditional models, durable buffering, and acceptance criteria.

Dataset naming policy: use letters, digits, hyphens (`-`), underscores (`_`),
and spaces only; avoid other special characters.

## Development standards

Follow [the development process](AGENTS.md) for every increment. Code must be
human-readable, with comments explaining non-obvious behavior. Returned errors
must identify the problem and provide safe, practical troubleshooting guidance.
See the [offline error review](docs/error-review.md) for current coverage.
These are review requirements; they do not imply that every existing error has
already been reviewed against the standard.

## Build and validate

Install a stable .NET 10 SDK. `global.json` permits stable .NET 10 feature-band
updates; SDK 10.0.401 was used for the first increment. Then run:

```sh
dotnet test IndustrialDataSim.slnx --configuration Release
dotnet run --project src/IndustrialDataSim.Cli -- validate-dataset "Line-1_Shift A"
dotnet run --project src/IndustrialDataSim.Cli -- validate-session examples/session-header.json
dotnet run --project src/IndustrialDataSim.Cli -- validate-simulation examples/constant-simulation.json
dotnet run --project src/IndustrialDataSim.Cli -- dry-run examples/constant-simulation.json
dotnet run --project src/IndustrialDataSim.Cli -- dry-run examples/ramp-simulation.json
dotnet run --project src/IndustrialDataSim.Cli -- dry-run examples/staircase-simulation.json
dotnet run --project src/IndustrialDataSim.Cli -- dry-run examples/random-staircase-simulation.json
dotnet run --project src/IndustrialDataSim.Cli -- dry-run examples/random-integer-hold-simulation.json
dotnet run --project src/IndustrialDataSim.Cli -- dry-run examples/sequence-simulation.json
dotnet run --project src/IndustrialDataSim.Cli -- dry-run examples/boolean-trigger-simulation.json
dotnet run --project src/IndustrialDataSim.Cli -- dry-run examples/boolean-gate-simulation.json
```

If using the optional project-local SDK installation, substitute
`.tools/dotnet/dotnet` for `dotnet`. Keep its CLI home and package cache under
the ignored tools directory when needed:

```sh
export DOTNET_CLI_HOME="$PWD/.tools/cli-home"
export NUGET_PACKAGES="$PWD/.tools/nuget"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
```

Validation commands emit one JSON object with `schemaVersion`, `valid`, and `errors`.
Diagnostics retain at most 100 errors plus one `validation.errors_truncated`
notice. Correct reported issues and rerun validation if that notice appears.
Every CLI response is capped at 4 MiB of UTF-8 JSON including its LF terminator;
oversized output is replaced by a small failure envelope, never partial JSON.
Exit codes are 0 for valid input, 1 for invalid input, 2 for incorrect usage,
and 3 for an unreadable session file.
Validation permits ASCII letters/digits, hyphens, underscores, and internal
ordinary spaces. It rejects surrounding whitespace and does not trim, rename,
or URL-decode input. This conservative project policy is not a complete statement
of what the server accepts. It does not check existence or reserve a Dataset.

The [session-header contract](docs/session-header-v1.md) documents the versioned
JSON fields and validation rules. It is not yet an executable simulation model.

The [constant simulation contract](docs/constant-simulation-v1.md) wraps that
header with generator definitions and a sampling interval. Its offline preview
is capped at 10000 candidate sample slots and 4 MiB of JSON; it never sends data to Historian.

The [ramp generator](docs/ramp-simulation-v1.md) adds numeric rates and optional
clamping bounds using the same sample clock. Use the
[combined schema](schemas/simulation-v1.schema.json) for all supported patterns.
The [staircase generator](docs/staircase-simulation-v1.md) adds explicit numeric
steps, fixed or seeded random dwell durations, and an optional total duration
cap with a final-value hold.
The [random-integer hold generator](docs/random-integer-hold-v1.md) fills an exact
duration with bounded holds and explicit adjacent-value behavior.
[Sequence steps](docs/sequence-v1.md) connect staircase and random-integer patterns
on one tag with exact handoffs and a local clock for each pattern.
[Boolean triggers](docs/boolean-triggers-v1.md) select and restart sequence
branches from another simulated tag’s True/False state.
[Boolean gates](docs/boolean-gates-v1.md) suppress output while False, with a
choice to pause or continue pattern time.

See the [implementation notes](docs/implementation-log.md) for completed scope
and the next small increment.

See [reproducible verification](docs/verification.md) for schema comparisons,
independent clock checks, and read-only validation of saved live-test evidence.

See [durable runtime v1](docs/durable-runtime-v1.md) for state transitions,
limits, restart semantics, and an example of driving generation and fake delivery.
See [runtime logging](docs/runtime-logging.md) for optional rotating logs,
plain-language troubleshooting, event codes, and logger health checks.

## Local configuration

Copy `.env.example` to `.env` and populate it locally. Connection-profile loading
has not been implemented yet. Keep `.env` owner-readable only. For deployments,
supply credentials through a secret manager or environment variables.

| Variable | Purpose |
| --- | --- |
| `TIMEBASE_BASE_URL` | HTTPS origin of the Historian service |
| `TIMEBASE_PULSE_URL` | HTTPS origin of the Pulse service |
| `TIMEBASE_CLIENT_ID` | OAuth client identifier configured in Pulse |
| `TIMEBASE_CLIENT_SECRET` | OAuth client secret; never commit it |
| `TIMEBASE_AUDIENCE` | Intended API audience configured in Pulse |
| `TIMEBASE_DATASET` | Destination Dataset name |
| `TIMEBASE_CA_BUNDLE` | Path to the deployment's trusted PEM CA bundle |

Obtain the public root CA from the service administrator. A server certificate
is distinct from its issuing CA. Chain and hostname verification both succeeded
with the supplied root CA during development. Keep TLS verification enabled.
Certificate files under `certs/` are local-only and excluded from Git. No
system-wide trust installation is required for a client-specific CA bundle.

## Ordering requirement

The simulator must write strictly increasing timestamps **per tag**, within and
across batches. No historical inserts, backward writes, or equal-timestamp
replacements are permitted. This is a simulator requirement: the tested
Historian accepted older points, so HTTP success cannot enforce this rule.

Read the latest timestamp before resuming an existing tag. Submit batches
sequentially per tag and coordinate writers to prevent races. After an ambiguous
write response or timeout, stop the affected session and preserve its payload and
tag ownership. Missing read-back points cannot authorize replay. See the
[delivery recovery policy](docs/delivery-recovery.md). Local durable reservations, forward progress, and conservative uncertainty handling
are implemented for simulated delivery. Server preflight and production transport
remain future work.

## Repository hygiene

Commit only placeholders and synthetic examples. Keep credentials, tokens,
private keys, deployment certificates, local configuration, screenshots,
diagnostic dumps, and logs outside version control. The example configuration
contains no working credentials or deployment-specific identifiers. Review
staged content before each commit; ignore rules alone are not a secret scanner.
