# Ramp generator v1

Ramps add one numeric pattern to the existing offline engine. They use the same
session header, sample grid, quality 192, preview limits, and CLI commands as
constants. A model may mix ramps and constants; it still has one generator per
declared output. No Historian access or persistent session state is introduced.

Use the [combined schema](../schemas/simulation-v1.schema.json) and
[ramp example](../examples/ramp-simulation.json). The previous constant-only schema
remains available as a stricter subset. Existing constant models and their output
are unchanged. Both `schemaVersion` and `generatorVersion` remain 1: this is an
additive pattern, not a change to constant semantics.

## Definition

```json
{
  "tag": "Example.Temperature",
  "kind": "ramp",
  "startValue": 10,
  "ratePerSecond": 2,
  "minimum": 0,
  "maximum": 15
}
```

| Field | Rule |
| --- | --- |
| `tag` | Exact name of an output declared as `number` |
| `kind` | Exactly `ramp` |
| `startValue` | Required finite number; value at pattern-local time zero |
| `ratePerSecond` | Required finite number; change per simulated second |
| `minimum` | Optional finite lower bound |
| `maximum` | Optional finite upper bound |

Positive rates rise, negative rates fall, and zero holds the starting value.
Either bound may be omitted. Optional means absent, not null. If both exist,
minimum must be less than or equal to maximum. Start value must already lie
inside the supplied bounds; it is not silently corrected. Equal bounds are
allowed and produce a hold while arithmetic remains finite.

Only the fields above are allowed. A constant's `value` cannot be supplied to a
ramp, and ramp fields cannot be supplied to a constant. Boolean and string outputs
cannot use ramps; numeric strings are not coerced to numbers.

## Time and arithmetic

At each sample, before bounds are applied:

```text
elapsedSeconds = patternElapsedTicks / ticksPerSecond
value = startValue + ratePerSecond * elapsedSeconds
```

The result is then clamped to the configured bounds. Clamping holds at a limit;
it does not stop the session, wrap around, reset, or emit an extra point at the
instant the limit is crossed. At one-second sampling the example yields:

| Elapsed seconds | Value |
| --- | --- |
| 0 | 10 |
| 1 | 12 |
| 2 | 14 |
| 3 | 15 |
| 4 | 15 |

The example session ends at second 5, which is excluded from the sample grid.
Its Boolean and string constant tags also produce five points each, for 15 total.

For a standalone ramp, pattern time is elapsed time since session start. Inside
[a Boolean gate](boolean-gates-v1.md), both modes wait for the first True.
`pauseAndSuppress` then counts only True intervals; `continueAndSuppress` counts
all elapsed time since that first True. Neither emits while False. In the gate
example at session second 10, the paused ramp is 4 and the continuing ramp is 8.
The sample timestamp still comes from the session grid.

Every evaluation derives from the supplied pattern clock, not the previous
sample's value. This avoids cumulative addition drift and makes shared samples
independent of sampling frequency. No wall clock, seed, or mutable generator
state is involved. Sequence children have their own step-local origins, but ramps
are not currently permitted as sequence children.

Ramps use IEEE 754 binary64 arithmetic. Fractional decimals can have rounding
error, and large integers may lose precision; unlike constants, ramp inputs are
converted to double for arithmetic. No decimal-exact or cross-version numerical
guarantee is claimed. Timestamp placement continues to use integer ticks.

Finite inputs can still overflow over a sufficiently long range. A non-finite
computed value rejects the entire preview with `dry_run.non_finite_value` and an
output-tag configuration path. Bounds do not hide overflow: finiteness is checked
before clamping. No partial JSON or partial tag data is returned. Configuration
validation checks the inputs, while `dry-run` checks actual sampled values.

## Commands

```sh
dotnet run --project src/IndustrialDataSim.Cli -- validate-simulation examples/ramp-simulation.json
dotnet run --project src/IndustrialDataSim.Cli -- dry-run examples/ramp-simulation.json
```

The output envelope and exit codes are unchanged. Preview limits remain 10000
candidate sample slots across all tags, before suppression, and 4 MiB of encoded JSON. The `data` member has Historian's
TVQ payload shape; the envelope is offline metadata, not a write request.

State-dependent rates, accumulation, and reset rules remain future work. This
ramp evaluates a supplied pattern clock; it is not a production-batch totalizer.
