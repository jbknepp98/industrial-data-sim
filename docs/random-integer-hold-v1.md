# Bounded random-integer holds

`kind: "randomIntegerHold"` produces seeded integer values for fixed or randomized
holds that fill an exact pattern duration. Output tags must be declared `number`.
See `examples/random-integer-hold-simulation.json` for values 21–29 over five hours,
with each planned hold between 15 and 30 minutes.

```json
{
  "tag": "Example.Temperature",
  "kind": "randomIntegerHold",
  "minimum": 21,
  "maximum": 29,
  "seed": 1,
  "durationMs": 18000000,
  "holdDurationRangeMs": { "minimum": 900000, "maximum": 1800000 },
  "adjacentValues": "requireChange",
  "afterDuration": "holdLast"
}
```

`minimum` and `maximum` are inclusive signed 32-bit integer literals. The seed is
an explicit integer literal from 0 through 4294967295. Exactly one timing form is
required: `holdDurationMs` or `holdDurationRangeMs` with `minimum` and `maximum`.
Durations are positive integer literals, at most 922337203685477 milliseconds.
Nulls, coercions, unknown properties, and missing fields are validation errors.

`durationMs` is measured from pattern-local zero and must be filled exactly.
Standalone patterns start at the session origin; sequence and gate wrappers supply
their documented local clocks.
Fixed holds must divide it exactly. Random bounds must admit a whole number of
holds; for example, 25 ms cannot fit holds of 10–11 ms. Infeasible plans fail
before emitting data; there is no shortened last hold. Equal dwell bounds act
like a fixed duration. A session may finish before this pattern does.

`adjacentValues` must be explicit:

- `allowRepeat`: a new choice may equal the previous choice. Scheduled holds still
  obey the duration bounds, but the unchanged visible value may span several holds
  and exceed the maximum individual hold duration.
- `requireChange`: every new choice differs from the previous value. This requires
  at least two values in the range, even if a particular plan has just one hold.

`afterDuration` currently accepts only `holdLast`: the final value continues
through session end after the exact scheduled duration. That continuation is
outside the bounded hold schedule. A containing [sequence](sequence-v1.md) instead advances to its next pattern
when this scheduled interval finishes.

## Planning and reproducibility

Let T be total duration, L the minimum hold, and U the maximum. Feasible hold
counts range from ceil(T/U) through floor(T/L). The supported maximum is 10000
holds per pattern. Choose a count uniformly from that feasible range intersected
with 1–10000; reject an empty intersection. This resource bound is part of v1
behavior. Resolved counts across all random-integer generators must also sum to
at most 10000 per model. Exceeding that total fails rather than changing a tag's
choices to accommodate another tag. Existing preview point/byte limits also apply.

For each hold with R milliseconds remaining and K later holds, choose an integer
dwell uniformly between max(L, R-K*U) and min(U, R-K*L). This reserves both the
minimum time and the maximum capacity of the remaining holds. The resulting
holds sum to T exactly, including the final hold. Earlier holds have first choice;
this is not uniform selection across all possible complete schedules.

Random draws use the SHA-256/little-endian/rejection algorithm documented in
[the staircase contract](staircase-simulation-v1.md). Independent stream names
are `integer-hold-count-v1`, `integer-hold-duration-v1`, and
`integer-hold-value-v1`. Count uses index zero; durations and values use the
zero-based hold index. Seed and stream conventions are part of generatorVersion
1. Duration choices do not consume value choices or vice versa.

For `requireChange`, draw uniformly from the value range with one fewer element,
then skip over the previous value. This avoids repeated retries and handles the
full signed 32-bit range. Equal bounds require no random draw.

The complete immutable schedule is resolved when loading the model. Sampling,
other tags, process execution order, and session length do not change it. An
unchanged definition and seed regenerate the same schedule; using the same seed
and settings for two tags creates matching schedules. The [durable runtime](durable-runtime-v1.md)
persists the configuration and cursor needed to reconstruct that schedule after restart. Changes to the count, bounds, seed, or duration can change the schedule.

## Sampling

Holds include their start and exclude their end. A sample exactly on a boundary
uses the next hold. Only the session's fixed sample grid emits points, at quality
192. No off-grid points are inserted. A coarse grid may miss short holds, and
observed dwell lengths can differ from planned dwell lengths by sampling effects.
Use a suitable sample interval; a five-hour one-minute preview contains 300 points
per tag, excluding the point exactly at five hours.

JSON Schema checks structure; runtime additionally validates range ordering,
exact-fill feasibility, integer-token spelling, output types, and resource limits.
