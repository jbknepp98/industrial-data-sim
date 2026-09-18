# Deterministic staircase generator

Use `kind: "staircase"` for a numeric tag with explicit step values and dwell
periods. The combined `simulation-v1.schema.json` includes this additive v1
pattern. Existing constant and ramp behavior is unchanged.

```json
{
  "tag": "Example.Temperature",
  "kind": "staircase",
  "afterSteps": "holdLast",
  "steps": [
    { "value": 0, "durationMs": 2000 },
    { "value": 10, "durationMs": 3000 },
    { "value": 20, "durationMs": 1000 }
  ]
}
```

At one-second sampling, an eight-second session yields
`0, 0, 10, 10, 10, 20, 20, 20`. The first step starts at the session origin.
Dwell intervals include their start and exclude their end; a sample exactly at
a boundary uses the next step. `afterSteps` is required and currently accepts
only `holdLast`: after all dwell periods, continue the final value until the
exclusive session end. A session may finish before the steps finish.

Sampling uses the existing fixed session grid. A boundary between sample times
becomes visible at the next sample; a short step entirely between samples may
not appear. No extra boundary points are inserted. Use a sufficiently small
sampling interval when each dwell must be visible.

Steps must form a nonempty array. Each requires a finite numeric `value` and a
positive integer-literal `durationMs`, or a `durationRangeMs` as described below.
Resolved total dwell duration may not exceed
922337203685477 milliseconds; validation rejects cumulative overflow. Unknown
fields, nulls, coercions, nonnumeric outputs, and unsupported end policies fail
validation. Values may increase, decrease, or repeat. JSON numeric tokens are
preserved, including integers beyond binary64's exact integer range; subsequent
Historian storage can have different precision.

Evaluation searches cumulative integer-tick boundaries from the session origin.
It keeps no mutable step cursor, so interleaved previews and different sample
frequencies agree at shared timestamps. The existing quality 192, 10000-point,
and CLI input/output size limits apply.

Run `dotnet run --project src/IndustrialDataSim.Cli -- dry-run
examples/staircase-simulation.json` for a mixed staircase/constant preview.
Looping, conditional transitions, faults, network writes, and durable execution
remain later increments.

## Reproducible random dwell durations

A step may instead specify an inclusive range of whole milliseconds:

```json
{
  "tag": "Example.Temperature",
  "kind": "staircase",
  "afterSteps": "holdLast",
  "seed": 42,
  "maxTotalDurationMs": 7200000,
  "steps": [
    { "value": 0, "durationRangeMs": { "minimum": 600000, "maximum": 3600000 } },
    { "value": 10, "durationRangeMs": { "minimum": 600000, "maximum": 3600000 } },
    { "value": 20, "durationMs": 900000 }
  ]
}
```

This holds 0 and 10 for randomly selected periods between 10 and 60 minutes,
then schedules a fixed 15-minute hold at 20. The combined scheduled dwell time
cannot exceed two hours. `holdLast` continues 20 afterward until session end;
the cap does not end the session or start another pattern. Reaching 20 occurs
at the start of its dwell, not at completion of the whole staircase.

Each step requires exactly one timing form. Range endpoints must be positive
integer literals, ordered minimum <= maximum, within the existing duration
limit. Equal endpoints give an exact dwell. A ranged model requires an explicit
`seed` integer literal from 0 through 4294967295, including equal ranges. A seed
on an entirely fixed model is rejected to avoid silently unused configuration.
No clock-derived seed is supplied. Fixed and ranged steps may be mixed.

`maxTotalDurationMs` is optional and uses the same positive integer duration
limits. Before resolving a schedule, validation sums fixed durations and range
minima; a total above the cap is an error. For each step, the engine reserves
all later minima, then draws within the current step's minimum and the smaller
of its maximum or the remaining available budget. Every step fits without
truncation. This is a sequential choice: earlier steps have first use of the
available time. It is not a uniform distribution over all feasible schedules.
The cap is an upper bound, not a requirement to fill the time exactly. Without
an explicit cap, the existing maximum representable duration remains the limit.

The schedule is resolved once when loading the definition, before sampling.
It depends on the seed, step positions, timing ranges, and total cap. Changing
the sample interval, session length, or other generators' ordering does not
change that schedule. Reusing a seed and identical timing configuration on two
tags intentionally produces matching schedules; use distinct seeds when this
correlation is unwanted. Editing/inserting steps may change subsequent timing.

### Version 1 timing algorithm

For each non-degenerate feasible range, hash UTF-8 text
`staircase-duration-v1:{seed}:{zeroBasedStepIndex}:{attempt}` with SHA-256. All
numbers use invariant decimal formatting; attempts start at zero. Interpret
the first eight digest bytes as an unsigned little-endian 64-bit integer. For
inclusive range width W, reject draws smaller than `2^64 mod W`, increment the
attempt, and try again. Otherwise select `minimum + (draw mod W)`. Equal bounds
need no draw. This avoids modulo bias without depending on runtime-specific
`Random` behavior. These conventions are part of `generatorVersion: 1` and
must remain stable for saved definitions.

The independent per-step choices require no shared mutable random stream.
This makes regeneration deterministic from an unchanged definition and origin;
it does not implement interrupted-session persistence or delivery recovery.
Random measurement noise remains future work. Randomized integer output values
are supported by the separate [random-integer-hold pattern](random-integer-hold-v1.md). JSON Schema checks shape; runtime additionally checks ordered ranges,
feasible total budgets, finite numbers, and integer-literal spelling.

See `examples/random-staircase-simulation.json` for a complete three-hour
preview with this two-hour staircase cap, one-minute sampling, and constants.
