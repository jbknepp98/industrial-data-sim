# Manufacturing process models

A simulation can supply `manufacturing` instead of `generators`. The existing
signal/gate/sequence models remain supported. Do not mix the two forms in one
session. See [the runnable example](../examples/manufacturing-simulation.json)
and [the schema](../schemas/manufacturing-v1.schema.json).

The example coordinates SKU routing between two mutually exclusive packaging
cells, readiness, Idle/Startup/Running states, variable speeds, a temperature
sensor with noise/faults, and two production totalizers. Batch production stops
the session at its target. Definitions are agent-authored JSON; there is no UI.

## Bounded windowed execution

Set `manufacturing.execution` to `"windowed"` for fixed-horizon models that need
multi-day operation. See the [windowed example](../examples/manufacturing-windowed-simulation.json)
and [Phase 2 qualification](phase-2-qualification.md). Omit this field to preserve
the original precompiled behavior, including production-target session termination.
Do not edit the definition of an admitted session to switch execution modes.

Windowed execution validates shape, dependency ordering and the initial process
tick at admission. Future expression failures are generation failures: the whole
provisional window is discarded and the prior durable cursor/state remains intact.
It does not promise to prevalidate future arithmetic. `stopWhen` and true
`stopOnTarget` are rejected in this mode; capped/whole-batch totals that do not stop
the session are supported. Use the precompiled mode for target-stopping sessions.

SQLite schema 8 commits a version-1 process checkpoint atomically with queued
output and its flattened cursor. It includes process time, previous underlying
values, state dwell, timers, batch counters/rates, target flags, frozen fault
values and the latest observations/quality. A checksum binds it to session,
immutable model hash and cursor. Partial timestamp rows and retries after byte
limits reuse the same evaluated tick. Horizon extension keeps the checkpoint;
it never restarts a process. No mutable state is inferred from Historian readback.

Each window advances at most 1,000 additional process ticks; sampling may span
at most 1,000 ticks. Existing node/expression-depth limits remain. Checkpoint JSON
is capped at 1 MiB; the entire horizon is not retained in memory. The old total
node-clock/expression-clock/observation-storage budgets apply to precompiled
mode only. These per-window guards are not arbitrary-load latency guarantees.
`dry-run` remains bounded and may require a shorter preview. `explain-process`
requires a short copy in precompiled mode; it refuses to label an empty future
trace as a completed explanation.

Missing, incompatible or damaged checkpoint state fails that session with
`manufacturing.checkpoint_integrity`. Preserve its database and ownership, restore
verified state, and never reset the cursor or regenerate submitted timestamps.
Checkpoints remain in SQLite after archive and are exported as a `processCheckpoint`
record in format 2. They are recovery state, not permission to replay an archive.

## Clocks, dependencies and reconstruction

`tickMs` is the explicit discrete process clock. `samplingIntervalMs` observes the
latest process tick. These are separate from HTTP batch size. Durations/offsets
inside the process must be whole multiples of `tickMs`. A finer sample interval
repeats the current process value; it does not advance the process. Choose a tick
fine enough for the equipment behavior you need to represent.

One node owns each declared output. Node order does not determine calculation
order: same-tick `{"tag":"Plant.Ready"}` dependencies are sorted and cycles are
rejected. `{"previous":"Plant.Total","initial":0}` deliberately reads the prior
process tick and permits delayed feedback. References are local to this session;
there is no live cross-session subscription. Dependencies see underlying process
values, before sensor noise or faults. To make a fault affect the process, model
it explicitly as a dependency/condition instead of a sensor fault.

In precompiled mode, recompilation deterministically reconstructs the process from its origin; SQLite
retains the normal generation/delivery checkpoints. Random streams use seed,
output tag, expression position and clock/batch index. Reordering nodes does not
reroll them. Renaming a tag or changing a pattern is a new model. HTTP batch size,
sampling frequency and restart do not change process evolution.

The default precompiled interpreter uses a bounded horizon: 1–32 outputs, at most
250,000 node-clock evaluations, 2,000,000 expression-clock evaluations, 4,096
expression nodes, 16 nesting levels, and 16 MiB of estimated observation storage.
Strings are at most 1,024 characters. These bounds deliberately limit CPU/memory;
large/high-frequency models must be split or use a coarser process tick. This is
not an unbounded event simulation. Runtime recompilation has a cost; existing
capacity measurements of simple signals do not establish process-model capacity.

## Expression vocabulary

Numbers, Booleans and strings are literal expressions; no implicit conversion is
performed. Objects use `op` and the fields below. Arithmetic must stay finite.

| Operations | Fields / behavior |
| --- | --- |
| `eq`, `ne` | `left`, `right`, same scalar type |
| `lt`, `lte`, `gt`, `gte` | Numeric `left`, `right` |
| `between` | Numeric `value`, inclusive literal `minimum`, `maximum` |
| `and`, `or` | `args`, 1–32 Boolean expressions |
| `not` | Boolean `arg` |
| `if` | Boolean `when`, same-type `then`, `else` |
| `add`, `subtract`, `multiply`, `divide` | Numeric `left`, `right`; division by zero rejected |
| `elapsedMs` | Local clock; session-relative outside a schedule |
| `stateElapsedMs` | Time since the containing state node entered its current state |
| `rising`, `falling` | Boolean `arg`; true for one process tick on an edge; initial previous value is false |
| `held` | Boolean `arg`, `durationMs`; true after continuous observed true time |
| `uniform` | Literal `minimum`, `maximum`, uint32 `seed`, `holdMs`; bounded real values |
| `ramp` | Literal `start`, `ratePerSecond`, `minimum`, `maximum` |
| `schedule` | Boolean `repeat`, 1–1,000 `steps` of `durationMs` and expression `value` |

Boolean operands and both conditional branches are evaluated each tick so timers
cannot depend on short-circuit order. A schedule evaluates its selected step on
a step-local clock. Repetition resets that local clock and repeats its seeded
pattern. A nonrepeating schedule holds its last local instant after its end.
Timer/edge expressions that were inactive for a process tick reset when next
observed; they do not count unseen time. Keep continuous interlocks in independent
nodes, rather than inside an intermittently selected schedule step.

## State machines

A node's `state` contains `initial` and 1–64 ordered `transitions` with `from`,
`to`, Boolean `when`, and optional `afterMs`. `from: "*"` matches any state.
At each tick the first eligible transition wins; only one state change occurs.
`afterMs` measures current-state dwell. A self-transition does not reset dwell.
Output type must be string. Keep higher-priority safety/interlock transitions
first. Predicates are evaluated every tick, including in states where their
transition is ineligible, so a `held` condition is independent of state dwell.

`explain-process <model.json>` emits up to 1,000 state/batch/target transitions,
the omitted count, and the effective end. It is an offline prediction, not a
Historian delivery receipt. Shorten the model for a complete bounded trace.

## Production accumulation and termination

An `accumulator` uses `initial` and optional Boolean `enabled`:

- `mode: "time"` adds the previous tick's nonnegative `ratePerSecond` over elapsed
  process time. A gate/rate change at a tick affects the following interval.
- `mode: "batch"` draws positive quantities from `quantityRange` and durations
  from `durationRangeMs`, using a uint32 `seed`. Durations are chosen uniformly
  from the inclusive tick-aligned range. Only enabled time advances a production
  batch; its quantity is added at completion. This is a simulated production
  batch, unrelated to a delivery batch.

Optional `target` requires `finalPolicy`: `cap` clamps exactly to target;
`wholeBatch` permits the final production batch to overshoot (batch mode only).
Accumulation stops there. `stopOnTarget: true` stops the entire session; otherwise
other tags keep running while the total holds. Root `stopWhen` can also stop the
session on an arbitrary Boolean expression. Both stop after computing that tick.

The final process values are held through the first observation grid point at or
after completion, which becomes the last candidate timestamp. If the configured
end does not include that observation, validation asks for a longer end or finer
sampling. No off-grid or beyond-horizon timestamp is invented. A target that is
not reached by the configured end simply reaches that time limit; inspect the
trace to distinguish these outcomes. Target-bounded sessions cannot be extended;
use a new explicit production session. Ordinary process horizons can extend only
if recompiling the longer model remains within the limits.

## Sensor noise and faults

Numeric nodes may add `noise` with nonnegative `amplitude`, uint32 `seed`, and
optional `minimum`/`maximum`. It is independent uniform noise per process tick.
Up to 32 `faults` use `startMs` and positive `durationMs`, with half-open intervals:
`freeze` holds the value first observed inside its interval; `quality` overrides
`quality` with a byte from 0 through 255. Default quality is 192. Overlapping
faults are applied in list order. These affect published observations only.

Verification includes sampling/batch/reload equivalence, declaration-order stable
randomness, state/timer boundaries, target completion, typed errors and quality.
The explicit `scripts/verify_manufacturing_production.py --write-test-dataset`
helper creates fresh Test tags and checks arrival indicators without replaying
writes. Credentials remain in the local connection environment.

Arithmetic diagnostics distinguish a zero denominator
(`manufacturing.division_by_zero`, at the right operand) from a nonfinite result
(`manufacturing.arithmetic_overflow`, at the operation). Correct the denominator
or reduce operand magnitudes. Both conditional branches evaluate eagerly, so an
`if` expression cannot guard an otherwise invalid division. Diagnostics identify
the operation and correction without echoing operand values.
