# Boolean gates: pause or continue without output

A `booleanGate` wraps a pattern and references a local simulated Boolean constant
or timeline tag. While False, it emits no TVQ point: no literal null, placeholder,
quality-only record, buffered sample, or later backfill. When True, it emits on
the existing session grid using the current simulated timestamp.

```json
{
  "tag": "Example.Response.Pause",
  "kind": "booleanGate",
  "triggerTag": "Example.Ready.Boolean",
  "whenFalse": "pauseAndSuppress",
  "pattern": { "kind": "ramp", "startValue": 0, "ratePerSecond": 1 }
}
```

The complete [example](../examples/boolean-gate-simulation.json) runs both modes
against the same Boolean timeline. `whenFalse` is required and accepts:

- `pauseAndSuppress`: the pattern clock advances only while the gate is True.
  On reopening it resumes where it paused, without resetting its seed or sequence.
- `continueAndSuppress`: after the first True, the pattern clock advances through
  later False intervals. Reopening emits the current pattern position. Hidden
  sample values are not generated for transport or buffered for later delivery.

Both modes wait at local time zero before the first True. An initially True
source starts immediately. An always-False source never starts and yields an
empty array for the gated output. Initial waiting is not counted as hidden
advancement, even in continue mode.

## Timing example

For a ramp starting at zero and increasing by one per second, let the gate be
False for seconds 0–2, True for 2–6, False for 6–10, then True again. At one-second
sampling:

| Session time (seconds) | Pause output | Continue output |
| --- | --- | --- |
| 0–1 | no points | no points |
| 2, 3, 4, 5 | 0, 1, 2, 3 | 0, 1, 2, 3 |
| 6–9 | no points | no points |
| 10, 11, 12 | 4, 5, 6 | 8, 9, 10 |

The resumption at 10 uses timestamp 10 in both modes. It does not re-emit timestamps
6–9 or resume with a stale timestamp. At an exact closing boundary, suppress;
at an exact opening boundary, emit. True duration includes all elapsed time,
not just a count of emitted samples. The pause clock at reopening is therefore
4, rather than the last sampled value's time of 3.

Open intervals and cumulative active time are derived from the immutable source
timeline during binding. Lookup uses simulated time, not previous sample state,
so changes between grid points still affect progress correctly. A True window
entirely between samples can advance a paused pattern without producing points.
Equal adjacent source states are one interval. The source's final state holds
beyond its final configured dwell, as in the existing Boolean timeline contract.
Output declaration order and other runs do not change results.

## Patterns and completion

Supported children are constant, ramp, staircase, randomIntegerHold, and sequence.
The child inherits the owning tag and its type; existing child type checks apply.
Gated constants can retain any supported scalar type. Sequences remain numeric.
Nested gates, Boolean switches, and Boolean timelines as children are unsupported.

Gate resumption never restarts a pattern or reselects random choices. A paused
sequence's step clock freezes; a continuing sequence can finish steps or its
entire schedule while suppressed. If it completes while hidden, reopening emits
its final held value under its existing holdLast policy. There is no implicit
rearming, looping, or fresh-sequence launch. This supersedes the earlier proposed
run-to-completion/rearm start gate for this implementation. `booleanSwitch` retains
its distinct restart-on-change behavior.

## Output and limits

Dry-run `pointCount` is the actual sum of emitted array lengths, which can differ
between tags. Suppressed slots never become entries in `data`. A fully suppressed
tag remains present with `[]` to make its declared output visible in the preview;
a future transport should omit empty tag arrays from write requests.

The existing 10000-slot preview limit applies conservatively to the full candidate
sample grid across all declared tags, before suppression. This bounds evaluation
work even when little is emitted; it is not a promise to accept a huge all-False
run. Existing byte limits and random-schedule allocation limits remain unchanged.
Suppression is checked separately before evaluation. A nullable internal arithmetic
failure still rejects the whole preview; it cannot silently become a missing point.
Closed slots do not evaluate pattern arithmetic. An emitted non-finite result
continues to fail. Continue mode advances the clock analytically without creating
hidden TVQ values; it is not a general event-execution engine for side effects.

Source references require exact spelling and a declared direct Boolean constant
or timeline. Unknown/self references, gated sources, invalid policies, missing
patterns, child tag overrides, and unsupported nesting fail validation. All child
configuration validates up front even if the gate never opens. No remote Historian
input polling, durable buffering, or delivery recovery is introduced here.
