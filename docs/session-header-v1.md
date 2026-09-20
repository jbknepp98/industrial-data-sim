# Session header v1

This is a small, offline configuration contract. A successful validation means
the header is well-formed, not that a complete simulation can execute. Generator
definitions, sampling, seeds, and preview limits belong to the implemented
simulation wrapper. Durable resource budgets and tag reservations are implemented
in the [runtime library](durable-runtime-v1.md), not this header validator.
Production-target end conditions remain future work. [Production commands](production-delivery-v1.md) resolve the connection profile separately from the model through environment settings.

The [constant simulation v1 wrapper](constant-simulation-v1.md) now provides the
first executable dry-run format while leaving this header contract unchanged.

The agent-authored document must match [the schema](../schemas/session-header-v1.schema.json)
and pass the runtime validator. See [the synthetic example](../examples/session-header.json).

## Fields

| Field | Rule |
| --- | --- |
| `schemaVersion` | Integer literal `1`; unsupported versions are rejected |
| `sessionId` | 1–64 ASCII letters, digits, hyphens, or underscores |
| `connectionProfile` | Reference ID using the same identifier rules; never a URL or secret |
| `dataset` | Dataset policy: ASCII letters/digits, hyphens, underscores, internal ordinary spaces |
| `startUtc` | UTC date-time ending in `Z`, including seconds, optionally 1–7 fractional digits |
| `endUtc` | Same format, strictly later than `startUtc` |
| `outputTags` | Nonempty array of objects, each with `name` and `valueType` |

The interval is half-open: start is included; end is excluded. Times with an
offset, even `+00:00`, are rejected in this version so agents have one canonical
UTC representation. Calendar validity is checked, including impossible dates.

An output's `valueType` is exactly `number`, `boolean`, or `string`. This declares
the JSON value kind, not a particular Historian numeric storage type. Null values
and integer-specific generator behavior are not part of this header contract.

Tag names are not subject to Dataset character restrictions: dots are allowed.
They must be nonempty and have no surrounding whitespace or control characters.
Duplicate names, including case-only differences, are rejected conservatively
until server case semantics are established. Names are preserved, not normalized.
This does not guarantee the server accepts every remaining tag-name character.

Every listed field is required. Unknown fields, duplicate JSON properties,
comments, trailing commas, malformed UTF-8, and unpaired Unicode escapes are
rejected. The CLI accepts an optional UTF-8 BOM, limits files to 1 MiB, and limits
JSON nesting to 32 levels. Embedded credential fields are unsupported; the
validator does not load `.env`, resolve a connection profile, or contact Historian.

## Command and diagnostics

```sh
dotnet run --project src/IndustrialDataSim.Cli -- validate-session examples/session-header.json
```

Output uses the existing response envelope:

```json
{"schemaVersion":1,"valid":true,"errors":[]}
```

Invalid input returns errors with `code`, `path`, and `message`. Independent
errors are collected so an agent can correct several fields in one pass. Paths
identify known fields and array indices; unknown property names are not echoed.
Supplied values, exception stack traces, and local file paths are not returned.

Exit codes: 0 valid, 1 invalid configuration, 2 incorrect CLI usage, 3 unreadable
input file. An invalid result never contains a partially usable session object.

## Schema versus runtime validation

JSON Schema describes structure and basic constraints. Consumers must enable
`date-time` format checking if their validator treats formats as annotations.
The runtime additionally enforces calendar/time-range rules, case-insensitive
tag-name uniqueness, duplicate-property rejection, input limits, and strict
integer-token syntax for `schemaVersion`. JSON Schema operates on parsed values
and cannot distinguish `1` from numerically equivalent spellings such as `1.0`.

The runtime validator is authoritative for this increment. Neither validation
layer reserves tags across sessions or checks existing Historian timestamps.
