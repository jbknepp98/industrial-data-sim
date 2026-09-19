# Finite sequence steps

A numeric `sequence` runs ordered patterns on one owning tag. This increment
supports `staircase` and `randomIntegerHold` children. It does not yet support
nested sequences, conditions, triggers, loops, constant children, or ramp children.
Existing standalone generators keep their behavior.

```json
{
  "tag": "Example.Temperature",
  "kind": "sequence",
  "afterSequence": "holdLast",
  "steps": [
    {
      "pattern": {
        "kind": "staircase",
        "afterSteps": "holdLast",
        "steps": [
          { "value": 0, "durationMs": 1000 },
          { "value": 10, "durationMs": 1000 },
          { "value": 20, "durationMs": 1000 }
        ]
      }
    },
    {
      "pattern": {
        "kind": "randomIntegerHold",
        "minimum": 21,
        "maximum": 29,
        "seed": 1,
        "durationMs": 18000000,
        "holdDurationRangeMs": { "minimum": 900000, "maximum": 1800000 },
        "adjacentValues": "requireChange",
        "afterDuration": "holdLast"
      }
    }
  ]
}
```

Only the sequence declares `tag`; child patterns inherit it and cannot override
it. A step has exactly one `pattern` property. Durations are derived from that
pattern, so there is no second competing step-duration field:

- A staircase finishes at the end of its final scheduled dwell. With random
  dwells, use the resolved duration, not `maxTotalDurationMs`. The cap remains
  an upper bound; it does not pad the staircase to fill the cap.
- A random-integer pattern finishes after its exact `durationMs`.

Standalone `afterSteps` and `afterDuration` fields stay required in children for
contract consistency, but their final-value continuation applies only when
running standalone. A containing sequence advances on schedule completion.
To hold the final staircase value longer before advancing, configure that final
dwell explicitly or add a one-step staircase hold as another sequence step.

## Timing and boundaries

For a standalone sequence, the first pattern starts at the session origin.
A Boolean gate or switch instead supplies its documented pattern-local clock. Each subsequent pattern starts
exactly when its predecessor finishes. Child elapsed time starts at zero at
that boundary; it never includes time spent in earlier patterns. Intervals are
half-open: a point exactly at handoff uses the next pattern's first value.
There is only one value for the tag at that timestamp, without an inserted or
duplicate boundary point.

The existing session sampling grid remains unchanged across handoffs. If a
boundary falls between samples, the next sample evaluates the new pattern at
its actual elapsed local time. Its clock is not delayed to that sample. Coarse
sampling can miss a short sequence step entirely. A session can end partway
through a sequence; all patterns are still validated up front.

`afterSequence` must be explicit and currently accepts only `holdLast`. After
the final scheduled interval, freeze its final scheduled value, not whichever
value happened to be sampled last. Thus changing sample frequency does not
change the post-sequence value. The session's end remains exclusive.

Seeds and child timing algorithms remain unchanged. Reusing the same seed and
configuration in two steps intentionally repeats the same local schedule.
Changing earlier steps shifts later absolute start times but does not consume
or alter their random choices. This immutable composition is reproducible,
not itself a persisted session runner. The [durable runtime](durable-runtime-v1.md)
persists the immutable configuration and cursor to resume sequence execution.

## Validation and limits

Use 1–1000 sequence steps and a numeric output tag. Unknown fields, child tag
overrides, unsupported patterns/end policies, malformed steps, and cumulative
tick-duration overflow fail validation. Nested random-integer patterns share the
same 10000-hold per-model budget with standalone random-integer patterns; each
pattern retains its existing limit. Existing CLI file/preview limits still apply.
JSON Schema validates structure; runtime additionally validates these combined
resource and arithmetic constraints.

`examples/sequence-simulation.json` combines a seeded 0→10→20 staircase capped
at two hours with exactly five hours of values 21–29, each held 15–30 minutes.
The eight-hour session leaves time to observe the final-value continuation.
It also includes independent constant tags. Run:

```sh
dotnet run --project src/IndustrialDataSim.Cli -- dry-run examples/sequence-simulation.json
```
