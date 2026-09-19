# Constant simulation v1: offline preview

The first executable model produces constant numeric, Boolean, and string tags
on a fixed sample grid. It does not load credentials, access Historian, reserve
tags, create a queue, or run a background session. A dry-run explicitly prints
generated values, so use synthetic configuration when sharing its output.

[Ramp generators](ramp-simulation-v1.md) are now also supported by the same engine.
This document and its original schema continue to describe the constant subset;
the [combined schema](../schemas/simulation-v1.schema.json) supports constants, ramps, staircases, random-integer holds, sequences, Boolean
triggers, and Boolean gates.

## Configuration

See the [example](../examples/constant-simulation.json) and
[JSON Schema](../schemas/constant-simulation-v1.schema.json).

| Field | Meaning |
| --- | --- |
| `schemaVersion` | Integer literal 1: executable configuration format |
| `generatorVersion` | Integer literal 1: supported generation semantics |
| `session` | Unchanged [session-header v1](session-header-v1.md) object |
| `samplingIntervalMs` | Integer from 1 to 2147483647, shared by all outputs |
| `generators` | Exactly one constant generator for every declared output |

Each generator has `tag`, `kind`, and `value`. `tag` must match its declared output
exactly, including case. `kind` must be `constant`. `value` must match the output's
JSON type: number, Boolean, or string. No nulls, arrays, objects, string-to-number
coercions, missing generators, duplicate generators, or extra fields are accepted.
Numeric values must convert to a finite binary64 number; serialization preserves
the original JSON numeric text rather than rounding it through double. This is
an offline representation guarantee, not a guarantee of Historian storage precision.

Both format and generator versions are explicit so future semantic changes can
be rejected rather than silently alter resumed output. Constants have no random
choices and do not accept seed fields. Seeded timing and value choices are
documented in the staircase and random-integer-hold contracts.

The executable format wraps the existing header rather than changing it. A header
alone still validates with `validate-session` but cannot execute as a simulation.

## Commands

```sh
dotnet run --project src/IndustrialDataSim.Cli -- validate-simulation examples/constant-simulation.json
dotnet run --project src/IndustrialDataSim.Cli -- dry-run examples/constant-simulation.json
```

`validate-simulation` checks structure and relationships without generating points
or reflecting configured values. It may accept a model too large for preview.
`dry-run` additionally applies preview budgets and generates the complete range;
it never silently truncates output. Use a separate preview copy of a large model
with a shorter range rather than treating a prefix as a completed simulation.

Input files retain the 1 MiB limit and UTF-8/JSON rules of the header command.
Exit codes remain 0 success, 1 invalid configuration or exceeded preview budget,
2 incorrect usage, and 3 unreadable input. Shared parsing failures retain the
existing `session.*` codes; model-specific failures use `simulation.*`, and
preview budgets use `dry_run.*`. Nested header paths begin with `$.session`.

## Sampling and output

Every tag starts at `startUtc`. Sample timestamps are computed as
`startUtc + sampleIndex × samplingIntervalMs`, strictly before `endUtc`.
The range is start-inclusive and end-exclusive. A positive range shorter than
one interval produces one point per tag. If the range is not an exact multiple
of the interval, the final point is the last grid point inside the range; no
extra endpoint observation is added.

Integer ticks preserve the start's sub-millisecond precision without cumulative
floating-point drift. There is no wall-clock pacing: this is deterministic
offline generation. Header output-tag order determines JSON member order;
reordering the generator declarations does not change the output.

All points currently have quality 192 (Good). Noise, faults, configurable quality,
per-tag sampling, and other patterns are deferred.

Successful output is one JSON object containing `schemaVersion`, `valid`,
`errors`, `mode` (`offline`), `sessionId`, `dataset`, `generatorVersion`,
`pointCount`, and `data`. The `data` object alone has Historian's tag-to-TVQ-array
shape. The surrounding preview envelope is not a Historian request body.
TVQs use `t`, `v`, and `q`, with UTC timestamps ending in `Z`.

The example emits nine points: three tags sampled at seconds 0, 1, and 2.
Constants are 72.5, true, and "Idle". It does not include second 3.

## Bounds and recovery limitations

- Maximum 10000 candidate sample slots across all output tags; checked before allocation.
  For constants each slot emits a point. Boolean gates can suppress slots; their
  reported pointCount counts only actual emitted points.
- Maximum 4 MiB of encoded UTF-8 output including the line ending; checked during
  serialization to a bounded buffer, before writing any result to stdout.
- Escaping and non-ASCII text count toward the byte limit. A low point count does
  not bypass this limit when constants or tag names are large.
- Limits are preview safeguards, not discovered Historian API limits or a future
  backfill capacity limit. Dry-run has no disk-backed queue or resume cursor;
  [durable execution](durable-runtime-v1.md) is provided separately.

Failures return only structured errors, not partially generated data. Repeated
dry-runs regenerate the same full preview; they do not advance persistent state.
