# Boolean-controlled sequences

This first trigger increment connects a simulated Boolean tag in the same session
to a numeric response tag. The trigger can be a `booleanTimeline` or Boolean
`constant`. The response uses `booleanSwitch`, with explicit False/True sequence
branches and `onChange: "restartBranch"`. Both tags are generated from the same
simulation clock and can be written together to the session's Dataset.

```json
{
  "tag": "Example.Trigger.Boolean",
  "kind": "booleanTimeline",
  "afterSteps": "holdLast",
  "steps": [
    { "value": false, "durationMs": 14400000 },
    { "value": true, "durationMs": 14400000 },
    { "value": false, "durationMs": 14400000 }
  ]
}
```

The complete runnable [example](../examples/boolean-trigger-simulation.json)
contains two numeric branches composed from staircase and random-integer patterns.
The response's shape is:

```text
kind: booleanSwitch
triggerTag: Example.Trigger.Boolean
onChange: restartBranch
whenFalse: { kind: sequence, afterSequence: holdLast, steps: [...] }
whenTrue:  { kind: sequence, afterSequence: holdLast, steps: [...] }
```

## Trigger semantics

At the session origin, the current Boolean state immediately starts its branch
at local time zero. Each actual state change interrupts the active branch and
restarts the selected branch from zero. Returning to a previous state restarts
its sequence; it does not resume it or select a new seed. Remaining in the same
state does not retrigger. Consecutive equal timeline steps are merged into one
activation. A completed branch holds its final scheduled value until the next
state change or session end. The timeline holds its final Boolean indefinitely
within the session after its final dwell.

Trigger intervals are half-open. At the exact transition timestamp, both the
Boolean and response reflect the new state, with the response at local zero.
The evaluator obtains the state and its activation time from the source's
resolved timeline, independent of output declaration order. It evaluates changes
at their actual simulated times, including changes between emitted samples;
coarse sampling can therefore miss a Boolean pulse while its reset still affects
a later response sample. The sample grid never inserts extra points or delays a
trigger to a sample timestamp. Changing the grid does not change values at shared
timestamps. Completed definitions remain immutable with no mutable activation
state shared between runs.

This is a local simulated dependency, not polling Historian for an external
input. It does not yet implement general expressions, numeric thresholds,
edge-only rules, debounce, pause/resume, queued completion, or arbitrary dependency
graphs. Those require explicit semantics in later increments. Separate [Boolean gates](boolean-gates-v1.md) now provide pause/continue suppression.
Neither behavior implements durable/resumable execution.

## Validation

- Timeline output type must be `boolean`, with 1–1000 explicit Boolean/duration
  steps and `afterSteps: "holdLast"`. Values must be JSON `true` or `false`, not
  0/1 or strings. Durations are positive integer-literal milliseconds whose
  cumulative ticks fit Int64.
- Switch output type must be `number`. The exact-case `triggerTag` must reference
  a declared local Boolean timeline or Boolean constant. Unknown, self, numeric,
  and unsupported sources fail validation. Restricted source types exclude cycles.
- Both branches must be valid sequences; branch tag overrides and nested switches
  are rejected. Branch sequence children retain existing supported pattern rules.
- Both branches are validated and allocated before execution, including a branch
  that never activates. Their random schedules share the existing model-wide
  10000-hold budget. Restarting a branch reuses its immutable schedule.
- All policies are required and unknown fields fail validation. Schema checks
  structure; runtime also binds dependencies and checks cumulative/resource limits.

## Live verification

See [the live trigger test](live-verification-boolean-trigger.md) for the ordered
24-hour write, exact tag names, transition values, and read-back results.
