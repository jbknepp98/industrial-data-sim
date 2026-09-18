# Timebase Historian API findings

These findings come from the development installation's OpenAPI specifications
and controlled API checks performed on September 17, 2026. They describe that
installation, not a guarantee about every release. The service version was not
captured. All tag names, timestamps, and values below are synthetic test data.

## Documentation and authentication

Paths relative to the appropriate service origin:

| Service | Path | Purpose |
| --- | --- | --- |
| Historian | `/api/help/index.html` | Swagger UI |
| Historian | `/api/v1.json` | OpenAPI specification |
| Pulse | `/api/v1.json` | OpenAPI specification |
| Pulse | `/auth/.well-known/openid-configuration` | Authentication discovery |
| Pulse | `/auth/token` | Token issuance |

Historian declares HTTP Bearer JWT authentication. A form-encoded Pulse token
request with `grant_type=client_credentials`, `client_id`, `client_secret`, and
`audience` returned an access token. Discovery advertised `client_secret_basic`
and `client_secret_post`; the latter was tested. The observed token lifetime was
3,600 seconds. A future client should use the returned `expires_in` and reacquire
a token before expiry. Never log token responses or Authorization headers.

Authenticated `GET /api/datasets/{dataset}/exists` returned HTTP 200 with JSON
`true`. Authentication, reads, and writes succeeded with TLS certificate and
hostname verification enabled using the deployment's root CA.

## TVQ payload

Dataset naming guidance supplied by the project owner: avoid all special
characters except hyphens (`-`), underscores (`_`), and spaces. Enforce this as
project validation policy and still URL-encode Dataset path segments. The
inspected OpenAPI name schema does not declare a character pattern or length
limit; do not misrepresent this guidance as a documented server restriction.

Use `POST /api/datasets/{dataset}/data` with `Content-Type: application/json`
and `Authorization: Bearer <access-token>`. The body maps tag names to arrays of
points. The separate `/data/{tagname}` write endpoint is marked deprecated.

| Field | Specification |
| --- | --- |
| `t` | Date-time string; examples include local time, UTC `Z`, and explicit offsets |
| `v` | Number, Boolean, or string; the schema also permits null |
| `q` | 32-bit integer OPC quality code; documentation identifies 192 as Good |

The TVQ object disallows additional properties. The schema does not explicitly
list required properties, but the simulator should always provide all three.
Use UTC with `Z` to avoid local-time ambiguity. Null and string writes were not
tested. Timestamp precision and limits remain unverified.

```json
{
  "Sim.Temperature": [
    {"t": "2026-09-17T22:00:00Z", "v": 72.5, "q": 192},
    {"t": "2026-09-17T22:00:01Z", "v": 72.6, "q": 192}
  ],
  "Sim.Running": [
    {"t": "2026-09-17T22:00:00Z", "v": true, "q": 192}
  ]
}
```

This write returned HTTP 200 with an empty body. Reads confirmed the numeric
points and returned the Boolean as numeric `1` with tag type `System.Boolean`.
The numeric tag type was `System.Double`. Both tags were created by the data
write without a preceding tag-creation call.

## Ordered batch experiment

A previously absent `Sim.Batch` tag received these requests sequentially.

Batch 1:

```json
{
  "Sim.Batch": [
    {"t": "2026-09-15T12:00:00Z", "v": 10, "q": 192},
    {"t": "2026-09-15T12:00:01Z", "v": 11, "q": 192},
    {"t": "2026-09-15T12:00:02Z", "v": 12, "q": 192}
  ]
}
```

Batch 2:

```json
{
  "Sim.Batch": [
    {"t": "2026-09-15T12:00:03Z", "v": 13, "q": 192},
    {"t": "2026-09-15T12:00:04Z", "v": 14, "q": 192},
    {"t": "2026-09-15T12:00:05Z", "v": 15, "q": 192}
  ]
}
```

Both returned HTTP 200, and all six timestamps, values, and quality codes were
verified by read-back.

## Explicitly authorized out-of-order experiment

A negative test subsequently sent older points:

```json
{
  "Sim.Batch": [
    {"t": "2026-09-13T12:00:00Z", "v": 20, "q": 192},
    {"t": "2026-09-13T12:00:01Z", "v": 21, "q": 192},
    {"t": "2026-09-13T12:00:02Z", "v": 22, "q": 192}
  ]
}
```

The API returned HTTP 200 with an empty body. Immediate historical read-back
returned all three older points, each with quality **193**, although 192 was
submitted. The latest point remained `2026-09-15T12:00:05Z`, value 15, quality 192.
The meaning of quality 193 is not established by the inspected documentation.
Do not assume it means rejection or treat these points as absent.

This installation accepted older data. Forward-only writes must be enforced by
the simulator. Unsorted points within a batch and mixed valid/invalid batches
were not tested. No cleanup was performed; the
historical test points remain in the development Dataset.

A subsequent September 14 batch at `12:00:00Z` through `12:00:02Z`, values
30–32, was also accepted and read back with quality 193. A single further
submission at `2026-09-14T12:00:02Z`, value 33 and quality 192, returned HTTP 200,
but immediate read-back retained value 32 and quality 193. This is one observed
duplicate-timestamp result, not a universal guarantee about overwrite behavior.

The Dataset schema also documents `ldt` (late data tolerance, milliseconds):
out-of-order data within this clock-skew window is discarded, while older data
beyond it can be accepted for backfill. `lda` limits the age of accepted late
data in days; zero rejects all late data, and the documented default is 30.
These settings explain why server acceptance is not equivalent to enforcing
the simulator's stricter forward-only policy. They do not establish the meaning
of quality 193 or fully explain the duplicate result without further testing.

## Reading and response differences

`GET /api/datasets/{dataset}/data` accepts repeated `tagname` query parameters
and optional `start`/`end` times. Without a time period it returns the latest
point. Responses contain `s`, `e`, and a `tl` array, whose entries contain tag
metadata in `t` and points in `d`. Queries may include a leading boundary point;
filter by timestamp when validating an exact interval.

Before the older write, the empty historical interval returned a placeholder
at `0001-01-01T00:00:00Z`, quality 0, with no value. A nonempty response array is
therefore not sufficient proof that requested data exists.

The tag-list specification describes an array, but the observed
`GET /api/datasets/{dataset}/tags` response was an object with `User` and `System`
arrays. The first batch-test preflight stopped without writing when it saw this
mismatch; the subsequent check handled the observed grouping.

## Remaining implementation questions

- Maximum points and bytes per batch, rate limits, and timeouts.
- Partial success, atomicity, idempotency, and safe retry semantics.
- General duplicate-timestamp semantics and the interpretation of quality 193.
- Numeric type selection, type changes, null handling, and string limits.
- Timestamp precision, range boundaries, and persistence after service restart.
- Concurrency control when other writers share a tag.

The generator and offline CLI are implemented; production delivery and recovery
remain pending. These observations do not constitute a complete integration suite.

## Unchanged values in a 24-hour live run

A subsequent engine-generated test submitted 8640 samples in twelve ordered
batches to six fresh Test tags. All writes returned HTTP 200; full-period reads
returned 3170 records. All value transitions matched. Numeric/Boolean constants
retained only their first point, and a bounded ramp omitted its unchanged plateau.
The string constant retained the first batch's 120 points but no subsequent
repeats. The cause and generality of this behavior remain unestablished.

HTTP success does not prove individual retention of repeated samples. Latest
stored timestamps can lag submission progress. Recovery needs an explicit policy
for unverifiable repeats and must not blindly replay them or mark them individually
verified from value equality alone. See [test details](live-verification-24h.md).
