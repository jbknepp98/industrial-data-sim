# String SKU tracking and exclusive routing

`stringTimeline` emits exact strings from 1–1000 positive, whole-millisecond
steps. Each step has `value` and `durationMs`; `afterSteps` must be `holdLast`.
The output must be declared `string`. Boundaries are half-open and independent
of sample interval, output declaration order, queue size and restart.

`skuRoute` emits a Boolean. Specify `skuTag` (a local stringTimeline), `readyTag`
(a local Boolean timeline or Boolean constant), and `sku` (an exact string).
Its value is true only when the source string equals `sku` using ordinal,
case-sensitive comparison and readiness is true. Unknown SKUs leave all matching
routes false. No string-to-number or Boolean coercion occurs.

For routes sharing the same SKU and readiness sources, each SKU may be claimed
only once. A duplicate claim fails validation before admission. This is a
single-source routing rule, not a plant-wide lock: routes built from different
sources require their own model-level exclusivity checks.

The loader resolves routes before Boolean gates/switches, compiling the union of
source boundaries into an immutable Boolean schedule. Gates and switches may
reference those routes. SKU/readiness sources cannot themselves be routes or
gates; nested dependencies, cycles and external/cross-session reads are rejected.
All dependent tags belong to the same session and Dataset.

When a route becomes false, `booleanGate` suppresses samples. Its
`pauseAndSuppress` mode freezes the pattern clock; `continueAndSuppress` advances
the clock after its first activation without buffering closed-gate values.
Neither emits null records. A Historian trend may draw a line across an omitted
interval or retain its last current value; inspect FeedEnabled alongside speed.
A switch instead selects its configured numeric sequence branch.

The reproducible model builder is `scripts/prepare_packaging_demo.py`. It creates
a new ignored model/manifest with fresh tags in `Test`, 72 hours of history and
seven more days of scheduled operation. It does not authenticate or publish.
The same sequence/seeds are used for each cell's pause/continue pair. These are
alternative behavior demonstrations, not two competing physical speed readings.

Run the bounded DemoPreview tool on the full model to produce its first 72 hours,
then `scripts/check_packaging_preview.py <folder>` to check every routing minute,
closed-gate suppression, ordering, quality, value range and visible clock-policy
differences. `scripts/verify_offline.py` runs the same check on a fixed-date model
in CI and deliberately corrupts one feed point to test the checker.
