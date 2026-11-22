<plan title="Phase 2: Certification compliance in the engine">

<overview>
Phase 2 of "Interview Front End for the Flow Data Simulator". Adds both certification storylines. Certification compliance came up in 5
customer meetings and was often the AI showcase ("which uncertified
technicians closed which work orders?").

- **3a:** an uncertified technician closed a safety-critical work order.
- **3b:** an operator ran a line or SKU without a current work-instruction
  certificate. This is the operator side, raised by discrete and chemicals
  prospects.
</overview>

<context>
Assumes phase 1 is complete and verified. Today technicians have `skills`; the
`certifications` table lists one row per technician and skill, with no
expiry. Assignment (`TryAssign` in `PlantEngine.cs`) only picks qualified
technicians on shift.
</context>

<changes-required>

<change component="Model">
**Files**: `PlantModel.cs`, `PlantModelLoader.cs`

New optional `maintenance.certification` section:

- `validDays`: how long a certificate lasts.
- `issued`: per technician and skill, a signed duration relative to the
  anchor (for example `"-300d"`). Expiry is issue plus `validDays`.
- `safetyCritical`: the failure codes that need a valid certificate.
- `whenNoneCertified`: `"wait"`, or `"assignAnyway"` (the story needs this:
  under pressure an expired technician takes the job).
- `renewals`: an optional list of technician, skill and when, so a lapse can
  be fixed partway through the history.
- `operators` (for 3b): `workInstructions[]`, each with an `id`, the line, the
  SKUs it covers, `validDays`, and per-operator issue times. Policy
  `whenOperatorUncertified`: `"runAnyway"` (the story) or `"supervisorSignoff"`
  (adds a sign-off row, and the run starts after `signoffMinutes`).

Validation rules:
- every technician/skill pair named exists;
- times are aligned to the minute;
- `assignAnyway` requires at least one safety-critical code.

Errors: `plant.unknown_certification_target`, `plant.certification_time`.
</change>

<change component="Engine">
**File**: `PlantEngine.cs`

- **Certificate state:** expiry per technician and skill, held in engine state
  and in the snapshot.
- **Assigning safety-critical tickets:** certified technicians are preferred.
  If none is free and on shift:
  - with `wait`, the ticket waits;
  - with `assignAnyway`, assign a skilled but uncertified technician, and
    record a compliance row with `certified=false` plus the reason
    (`"certificate expired 2026-11-02"`).
- **Calendar events:** issue, expiry and renewal events update the state and
  write `trainingrecords` changes at their times.
- **Operator certification (3b):** when a run starts, check that the shift's
  operator holds a current certificate for that line and SKU's work
  instruction. If not, apply the policy and write an `operatorcompliance` row
  (run_id, operator_id, work_instruction, certified, reason). With
  `rejectPercentWhenUncertified` set, an uncertified run gets a higher reject
  rate. That ties the story to first-pass yield and the RejectCount tag.
- **Engine version:** bump `EngineVersion` to 2, because the snapshot layout
  gains certificate state.
- **Golden hashes must not move.** Models without the section produce
  byte-identical output. Event ordering keys for the new calendar events use a
  new calendar group placed after the existing ones, so existing ordering is
  untouched.
</change>

<change component="Schema, spec, preview">
**Files**: `PlantSchema.cs`, `PlantConnectSpec.cs`, `PlantPreview.cs`,
`tools/plant-preview/index.html`

Tables, only when the section is present:
- `trainingrecords`: record_id, employee_id, skill, issued_utc, expires_utc,
  status.
- `workcompliance`: compliance_id, ticket_id, technician_id, skill,
  safety_critical, certified, reason, checked_utc.
- `workinstructions`: instruction_id, line_id, sku, valid_days (only when
  operator certification is configured).
- `operatorcompliance`: compliance_id, run_id, operator_id, instruction_id,
  certified, reason, checked_utc, signoff_utc.

Spec additions: connect-spec relationships, plus query properties:
- `Person.ExpiredCertifications`;
- `Equipment.UncertifiedWork`;
- `Line.UncertifiedRuns`.

Preview: the summary adds `uncertifiedAssignments`, and the page lists them.

No new tags. The story is relational, joined to the existing unit Status and
Vibration tags at the ticket times.
</change>

<change component="Library and docs">
- Storyline 3a becomes `"engine": "available"`, with a fragment that plants a
  lapse about 10 days before the anchor on the technician who covers night
  shift, and a safety-critical failure on the filler valve about 3 days before
  the anchor. Its talk track uses the Atlas or Gage question "Which
  safety-critical work orders were closed by technicians whose certificate had
  expired?" plus the Explorer view of the unit.
- Storyline 3b becomes available. Its fragment lets one operator's certificate
  for the Berry SKU's work instruction lapse about 6 days before the anchor,
  with `runAnyway` and a higher reject rate. Its talk track:
  1. Explorer: RejectCount on L2 during Berry runs.
  2. Connect: `operatorcompliance` joined to `productionruns`.
  3. Gage: "Which runs were operated without a current certification, and
     what was their reject rate?"
- `docs/plant-simulation.md`: a Certification section, and the
  `EngineVersion` 2 note.
</change>

<change component="Tests">
`PlantCertificationTests.cs` (new):

- **Lapse:** an expired technician is never chosen while a certified one is
  free.
- **Assign anyway:** with `assignAnyway` and no certified technician available,
  the uncertified assignment happens at the planted time and writes
  `workcompliance` with `certified=false`.
- **Wait:** with `wait`, the ticket waits, and the downtime lasts longer
  accordingly.
- **Renewal:** a renewal restores compliance.
- **Operator run anyway:** a run started by an uncertified operator writes
  `operatorcompliance` with `certified=false` and applies the reject uplift.
- **Operator sign-off:** `supervisorSignoff` delays the run start by the
  sign-off time.
- **Golden hashes:** models without `operators` are unchanged.
- **Loader errors:** each one has a test.

`PlantSnapshotTests`:
- add a certification model to `Instants()`, so resume equivalence covers the
  new state;
- the golden hashes stay as they are;
- the "damaged or other version" test now uses version 2.

The live Postgres test: tables are created, rows match the engine, and remove
drops the new tables.
</change>

</changes-required>

<success-criteria>
<automated-verification>
- [ ] `dotnet build` / `dotnet test` (Release) with `FLOWSIM_TEST_PG` set, all passing
- [ ] `PlantSnapshotTests.EngineOutputIsUnchangedForPlantsAlreadyRunning` unchanged and passing
- [ ] Storylines 3a and 3b compose, validate and run in `PlantStorylineTests`
</automated-verification>

<end-to-end-verification>
- [ ] Build and run a `PlanTestCert…` plant (CLI, Docker dev image) with storylines 3a and 3b. `operatorcompliance` shows the uncertified Berry runs, and their RejectCount slope in Historian is steeper (verify script, 0 mismatches). Also, after `plant.live`, `SELECT * FROM <prefix>workcompliance WHERE NOT certified` returns the planted row with the expected ticket and technician
- [ ] Restart that container: it logs `plant.snapshot_ignored` once (old layout) or `snapshot_resumed`, and the rows stay identical
- [ ] BluebirdBev (no certification section) restarted on the new image: it logs `plant.snapshot_ignored` (version 1 → 2), then resumes snapshots, with no `history_mismatch`, and the Historian check shows 0 mismatches
- [ ] Remove the `PlanTestCert…` simulation
</end-to-end-verification>
</success-criteria>

<post-verification>
Commit: "plant: technician certification compliance (EngineVersion 2)". Proceed to phase 3.
</post-verification>

</plan>
