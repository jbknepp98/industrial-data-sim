# Boolean trigger live verification

Dataset: `Test`. Period: September 17, 2026, 00:00 inclusive through September 18,
00:00 UTC exclusive. Sampling: one minute, 1440 samples per tag.

- Trigger: `Sim.TriggerDemo.Run20260918T044313Z.Trigger.Boolean`
- Secondary: `Sim.TriggerDemo.Run20260918T044313Z.Response.Sequence`

The Boolean is False at 00:00, True at 04:00, False at 08:00, True at 12:00,
False at 16:00, and True at 20:00 UTC. Each state activates its own sequence.

| Offset from activation | False branch | True branch |
| --- | ---: | ---: |
| 00:00 | 0 | 100 |
| 00:20 | 5 | 150 |
| 00:40 | 10 | 200 |
| 01:00 | 17 | 209 |
| 01:15 | 17 | 205 |
| 01:30 | 13 | 201 |
| 01:45 | 13 | 206 |
| 02:00 through next trigger | 13 | 206 |

Each branch finishes its scheduled two hours and holds its final value for the
remaining two hours before the trigger changes. The first hour is a staircase;
the second hour uses the seeded random-integer hold generator. The selected seeds
resolve to two 30-minute holds for False and four 15-minute holds for True. Each
branch activates three times, with identical local schedules on reactivation.

## Checks and observed result

All 304 Release tests passed before writing. An independent Python calculation
checked all 2880 compiled C# preview points, including trigger states, response
values, timestamps, and quality. Repeated previews were byte-identical. All seven
examples passed schema validation; malformed configurations and invalid trigger
references failed the CLI without partial output.

A one-shot local transport probe submitted the generated data in twelve ordered
two-hour batches, 240 points per batch. Fresh tag names were checked before the
first write. TLS verification remained enabled. All writes returned HTTP 200;
no request was replayed. Existing tags and Dataset settings were not changed.
Payloads, raw read-backs, and the request manifest remain in ignored local state.

Full-period read-back returned six trigger records and 36 secondary records.
All 42 records matched expected timestamps, values, and quality 192; every
expected state/value transition was present. Stored types were System.Boolean
and System.Double. The Boolean raw API representation was 0/1. Latest retained
values also matched. Unchanged repeated samples were omitted, so 2880 submitted
samples do not establish 2880 individually retained records. This preserves the
previously documented distinction between submitted progress and stored records.

This verifies local simulated Boolean-to-sequence behavior and its generated
Historian data. It does not establish external-Historian-trigger polling,
production writer recovery, or service-restart durability.
