# Repeating patterns (1.1.0)

These finite patterns now accept `repeat` as their end policy, as well as
`holdLast`:

- `staircase.afterSteps`
- `randomIntegerHold.afterDuration`
- `sequence.afterSequence`
- `booleanTimeline.afterSteps`
- `stringTimeline.afterSteps`

`repeat` restarts the pattern on a fixed cycle, so data keeps moving on long or
open-ended sessions. See [open-ended sessions](design-open-ended-sessions.md).
`holdLast` behaves exactly as before.

## Cycle length

| Pattern | Cycle |
| --- | --- |
| `booleanTimeline`, `stringTimeline` | Sum of step durations |
| `staircase` with fixed `durationMs` steps | Sum of step durations |
| `staircase` with any `durationRangeMs` step | `maxTotalDurationMs`, which is required. The final step's value holds until the cycle ends. |
| `randomIntegerHold` | `durationMs` |
| `sequence` | Sum of each step's cycle length (as above) |

A random staircase without `maxTotalDurationMs` cannot repeat, and is rejected
with `simulation.repeat_needs_fixed_cycle`. Steps inside a sequence cannot
repeat on their own (`simulation.repeat_in_sequence_step`): put `repeat` on the
sequence. In a repeating sequence, each step occupies its fixed cycle length.

## Random draws

Cycle 0 is identical to the same pattern with `holdLast`. Each later cycle is
planned from its own deterministic stream: the generatorVersion 1 stream name
plus `#cycle<n>`. So:

- **Cycles differ.** Random holds and dwells change from cycle to cycle.
- **It's reproducible.** A value depends only on the model and elapsed time, not
  on sampling, generation window size or restarts.
- **Each cycle is a complete, valid plan** within its bounds, and hold counts
  per cycle stay within 10000. The model-wide 10000-hold limit is checked
  against cycle 0.

Existing models cannot change output, because `repeat` was previously rejected.
generatorVersion therefore stays 1.

## Dependencies

- **Switches** (`booleanSwitch`): when a repeating trigger has the same state
  at the end of one cycle and the start of the next, it is one continuous
  activation. The branch clock does not restart at the boundary.
- **Gates** (`booleanGate`): with a repeating trigger, `pauseAndSuppress` adds
  the open time of every completed cycle, and `continueAndSuppress` runs from the
  first opening.
- **SKU routes:** the SKU timeline and a readiness timeline must both repeat or
  both hold (`simulation.route_repeat_mismatch`). Routes with two repeating
  sources compile one shared cycle, the least common multiple of the two, limited
  to 20000 intervals (`simulation.route_cycle_limit`). A Boolean constant
  readiness works with either policy.

Tests in `RepeatPatternTests` compare each fixed repeating schedule with the same
steps written out many times with `holdLast`, including routes, both gate modes
and switch clocks. They also check random cycles across window sizes and reloads.
