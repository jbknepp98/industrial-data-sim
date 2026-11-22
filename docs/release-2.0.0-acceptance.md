# Version 2.0.0 acceptance matrix

Read with the [release specification](release-2.0.0-specification.md). State as of
November 22, 2026, with `v2.0.0-rc.1` tagged on `a7bceef`.

States: **Passed** means the evidence below was recorded on the 2.0 code (rc.1 or the
`2.0.0-dev` builds it was cut from, where noted). **Partial** means part of the gate is
evidenced and the rest is named. **Open** means no evidence yet. Unit tests alone never
make a live gate Passed.

| ID | Gate / pass condition | Evidence | State |
| --- | --- | --- | --- |
| P01 | Scope, invariants, contract freeze and deferred features written down | [Release specification](release-2.0.0-specification.md) | Passed |
| P02 | Same model, seed and anchor give the same history; a resumed run equals a run from the start | `PlantSnapshotTests` golden hashes and every-row, every-tag comparisons at many snapshot instants; live restarts below matched the engine | Passed |
| P03 | Rows exactly once in Postgres across crashes and restarts | Rows and `applied_sequence` commit in one transaction; tests show the tables equal the engine history however many steps it is applied in, and that replaying further never rewrites earlier rows; live graceful restarts of PlanTestBevA and PlanTestChemB2 resumed with 0 row differences (712 and 716 rows equal to the engine) | Partial: no live crash drill (process killed mid-transaction) |
| P04 | Every Historian tag point equals the engine | Live, 0 mismatches: BluebirdBev 139,305 points; PlanTestBatch 33,403; golden 5a/5b/5c 116,844, 120,364, 123,455; PlanTestBevA 125,220; PlanTestChemB2 116,334 | Passed |
| P05 | A batch interrupted by a kill is resent unchanged | Tests of `sending_until_utc` and the resend path; live stops were graceful (0 resent batches) | Partial: a live `docker kill` drill is open |
| P06 | After sleep or a long stop, the missing period is delivered as if the plant had run | Simulated-clock tests of catch-up in 6-hour steps | Partial: a real laptop sleep and a hard power loss in Docker are open |
| P07 | A simulation touches only its own tables; removal drops only its own | Admission and removal tests; live removal from the page dropped 29 tables per plant and left `bluebirdbev_*` and `flowsim_*` untouched | Passed |
| P08 | One host per simulation; another process's plant is never written | Advisory-lock tests; `bluebirdbev-plant` shown `running_elsewhere` throughout phase 8 and the rc.1 drill, with no writes from the web container | Passed |
| P09 | Interview reaches Claude only through Pulse Gage; failures explain the Pulse setting and stop | `GageClient` tests for every `gage.*` code, the origin guard and the credential guard; live `plant gage-check` passed (rc.1 drill and phase 8); `gage.missing_setting` explained in the CI image smoke | Passed |
| P10 | Approval stays human | Test asserting the interview has no build or run tool; plants in acceptance were admitted only by the page's approve call | Passed |
| P11 | The interview proposes fitting plants for the target audiences | Beverage audience → storylines 1, 2, 5b on `beverage-syrup-batch`; chemicals audience → 5a, 3a, 3b on `chemicals-batch` | Passed, with a recorded limit: the beverage prompt without a syrup mention ranks 6 over 5b |
| P12 | Every step in a demo guide shows what the guide says | Explorer and Connect steps checked for both audience plants (Historian, Explorer MCP, SQL as `flowsim_reader`) | Partial: Gage and Atlas steps not driven |
| P13 | Page access is limited as documented | Tests: host allowlist (`web.host_not_allowed`), cross-origin refusal (`web.cross_origin`), JSON and 64 KiB limits; compose publishes on `127.0.0.1` only | Passed; the page has no login (accepted limit, see specification) |
| P14 | Container lifecycle: healthy start, SIGINT stop within the grace period, resume from snapshots | CI smoke on both architectures (healthy, exit 0 on stop); live phase 8 restart: both plants resumed from their 07:00 snapshots, live within a second, nothing resent | Passed |
| P15 | Fresh install from the README with a CI-built image | rc.1 drill, November 22, 15:49 UTC: CI arm64 image loaded (checksum OK, label 2.0.0-rc.1), `plant gage-check` passed, healthy in 6 s, `/api/health` ok, BluebirdBev `running_elsewhere`. CI images now carry the version tag (`b5dd016`) | Passed for linux-arm64; linux-x64 has the CI smoke only |
| P16 | CI passes on the exact candidate | Run 44999977188 on `a7bceef`: offline ×3 OS, packages ×4 RIDs with dependency audits, web-image ×2 architectures | Passed |
| P17 | The two audience plants run for several days on the candidate image and still equal the engine | — | Open |
| P18 | Purge age: the backfill warning and the history-length guard work | Tests for `plant.retention_warning` and `tool.backfill_too_short`; the guard refused a 6-day 5b and an 11-day 5a in phase 8 | Passed (the live warning line was not separately recorded) |
| P19 | The 1.x session simulator is unchanged | Its tests pass in the same CI run; no change under its runtime paths | Passed |
| P20 | Documentation current: install, operator reference, codes, compatibility, handoff | November 22 pass: this specification and matrix, the 2.0 handoff, CLAUDE.md, README, compatibility record; the codes reference and an overview are drafted as KB articles kept outside git | Partial: further KB articles (install, interview, library, operating plants) and reorganizing plant-simulation.md as a user guide are open |
| P21 | Owner approval of a bare `2.0.0` | — | Open |

## Evidence records

Each live row's evidence is recorded where it was produced:

- phase 8 results and token cost: [plant-simulation.md](plant-simulation.md#packaging-and-live-acceptance-phase-8);
- earlier live checks (milestone 3, batch units, golden batch): the same document, under
  their sections;
- CI runs: the links in [release-notes.md](release-notes.md).

A live check records the image, the Dataset and its purge age, the simulation name
(`PlanTest…`), the number of points and rows compared and the mismatches. Tag checks
compare Historian with `plant preview` at the stored anchor, using a preview horizon of
the elapsed time plus 12 hours so the live look-ahead is covered. Row checks use the
shortest preview, because a batch's change from Released to Consumed has no timestamp
column.

## Completion rule

2.0.0 is released when every row is Passed, or Partial or Open with the owner's recorded
acceptance of the gap in the release notes, and P21 is Passed. A behavior-changing fix
after a row passed invalidates that row's evidence; repeat it on the new candidate.
