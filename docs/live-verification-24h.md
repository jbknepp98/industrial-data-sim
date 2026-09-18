# 24-hour live verification

The compiled C# generator produced a full UTC day, September 17, 2026,
00:00 inclusive through September 18, 00:00 exclusive, sampled every minute.
Six fresh tags exercised four categories in Dataset `Test`: constants (three
scalar types), ascending ramp, descending ramp, and bounded ramp.

Tag prefix: `Sim.Verify24h.Run20260918T023427Z`.

| Tag suffix | Pattern | Submitted samples | Returned records | Final value |
| --- | --- | ---: | ---: | --- |
| ConstantNumber | Constant 72.5 | 1440 | 1 | 72.5 |
| ConstantBoolean | Constant true | 1440 | 1 | true (raw 1) |
| ConstantString | Constant Running | 1440 | 120 | Running |
| RampUp | 0 + 0.01 per second | 1440 | 1440 | 863.4 |
| RampDown | 1000 - 0.01 per second | 1440 | 1440 | 136.6 |
| RampBounded | 0 + 0.01 per second, bounded 0–100 | 1440 | 168 | 100 |

Twelve sequential two-hour batches each submitted 720 points. All returned
HTTP 200. Each batch was submitted once; per-tag timestamps strictly increased
within and across batches. No existing tags or Dataset settings were changed.
The final scheduled sample was 23:59 UTC.

## Verification and limitations

An ignored one-shot Python transport probe submitted the exact C# preview
payload. This is integration evidence for existing generators, not a production
writer or durable recovery implementation. Payloads, responses, and a request
manifest remain in ignored local state; credentials were not included in them.

Strict sample-count verification stopped after the first batch. Read-only
reconciliation confirmed one numeric and one Boolean constant record, but all
120 initial string records. After the second batch, another reconciliation
confirmed no further string records. Neither acknowledged batch was replayed.

Subsequent checks required every generated value or quality transition. Every
returned record had to match a submitted timestamp, value, and quality, in
strict timestamp order; omissions were allowed only for unchanged repetitions.
A final full-period read and latest-value read passed these checks. All 3170
returned records had quality 192 and the expected stored types: System.Double,
System.Boolean, or System.String. Numeric comparisons used 1e-12 relative and
1e-9 absolute tolerance. Boolean true read back as numeric 1.

The bounded ramp reached 100 at 02:47 UTC; its plateau was omitted thereafter.
Numeric and Boolean constants retained only 00:00; the string retained
00:00–01:59. These are observations on this installation, not an established
universal compression contract. The underlying reason for the string's different
initial and subsequent batch behavior remains unknown.

8640 submitted samples therefore do **not** establish 8640 individually stored
records. Missing repeated values cannot be individually verified by this query.
The latest stored timestamp can lag the last submitted timestamp. Future durable
recovery must not equate it with a delivery checkpoint or infer successful
receipt of omitted samples from a matching value. This test does not establish
exactly-once delivery, throughput limits, or persistence across server restarts.
