<plan title="Phase 1: Storyline library and interview script">

<overview>
Phase 1 of "Interview Front End for the Flow Data Simulator". Turns the needs
analysis into versioned data that both front ends use, the Claude Code skill
now and the web interview later:

- the 15 storylines (who they are for, what they show, how to build them);
- the interview script (questions in a need-first order);
- a demo-guide template.

The plant model format and the engine do not change in this phase.
</overview>

<context>
Starting point: `e651155` or later (snapshots are in). The needs analysis is in
`customer-needs-analysis.md`, next to this plan. Quality/Shipping are the only
optional areas; certification and batch come in phases 2–4, so their
storylines are marked `"engine": "planned"` here.
</context>

<changes-required>

<change component="Storyline library (data)">
**File**: `examples/storylines/storylines.json` (new). It ships with the CLI and
web images, next to `examples/plants`.

The library has `schemaVersion: 1`, a `kind` of `"storylines"`, and one entry
per storyline (15). Fields of each entry:

- **Identity:**
  - `id` (kebab-case, for example `labeler-jams-late-orders`);
  - `title`, plus a `pitch` of one sentence in the audience's words.
- **Who it is for:**
  - `industries` (fixed list: food-beverage, cpg, packaging, chemicals, pharma,
    discrete, metals, mining, water-energy);
  - `roles` (plant-manager, maintenance, reliability, ci-oee, quality,
    process-engineer, it-ot-architect, controls, executive, ehs);
  - `kpis`;
  - `evidence`: meeting count from the needs analysis.
- **What it needs:**
  - `systems`: what the data imitates, for example "SAP PM work orders",
    "Ignition tags";
  - `engine`: `available`, `planned` or `later`, plus `requires` (areas, for
    example `["quality"]`).
- **How to build it:** `base`, the base model id, plus `fragment`, a JSON
  Merge Patch (RFC 7386) applied to the base. It plants the story: failure
  modes, incidents with relative times, excursions, and so on. Arrays in a
  merge patch replace the base array, so a fragment that touches
  `maintenance.failureModes` carries the full list. **Implementation note:**
  the planted-event lists (`incidents`, `quality.excursions`) add up across
  storylines instead of replacing each other, because otherwise no two
  storylines could be combined. Phases 2–4 add their planted-event lists to the
  same rule.
- **How to show it:** `talkTrack`, 3–6 steps. Each step has a tool (Explorer,
  Connect, Atlas or Gage), what to open (tag names or tables, using `{TagRoot}`
  and `{prefix}` placeholders), the question to ask, and what the audience
  should see.

**File**: `examples/storylines/bases/beverage-2-line.json` (new). This is the
current Bluebird example with the simulation name, tag root and seed
parameterized by the interview. Later phases add `chemicals-batch.json`
(phase 3).

**Coverage when this phase ends:**
- **Available:** 1 (labeler jams → late orders), 2 (repeat bearing failure),
  6 (quality hold traced to shift and sensor).
- **Planned, certification (phase 2):** 3a (uncertified technician closes a
  safety-critical work order), 3b (operator runs without a current
  work-instruction certificate).
- **Planned, golden batch (phase 4):** 5a (chemicals reactor drift), 5b
  (beverage syrup batching drift, then filler holds), 5c (pharma deviation
  with an audit trail).
- **Later:** 4, 7, 8, 9, 10, 11, 12. They are listed so the interview can say
  "not yet" honestly and record demand.
</change>

<change component="Library loader">
**File**: `src/IndustrialDataSim.Core/Plant/PlantStorylines.cs` (new)

- `PlantStorylines.Load(json)` uses the same strict style as the model loader:
  unknown fields are errors, and the fixed lists are checked.
- `Compose(baseJson, storylineIds, overrides)` applies the fragments in order
  (RFC 7386 merge), then the interview's overrides (simulation name, seed,
  lines, people). It returns model JSON for `PlantModelLoader`.
- Two fragments that set the same path with different values is a
  composition error: `storyline.conflict`, naming both storylines and the path.
</change>

<change component="Interview script">
**File**: `examples/storylines/interview.md` (new). The script both front ends
follow, written as instructions to the interviewer:

1. **Audience first:**
   - Who will watch: roles, and whether leadership will be there.
   - Industry and product.
   - The one or two problems that cost them the most. Use the KPI words from
     the library.
2. **Their systems:**
   - ERP, CMMS, MES or LIMS, historian, SCADA. SAP and Ignition are the
     common answers, so offer them as defaults.
   - These shape the table and tag naming notes in the demo guide.
3. **Constraints:**
   - AI and network: say up front that the plant runs locally and that the
     demo's AI uses their Gage provider.
   - How many days of history they need (it must fit the Dataset purge age).
4. **Storylines:**
   - Propose 1–3 from the library, ranked by industry, role and KPI overlap.
   - Explain each in their words, using the `pitch`.
   - Say plainly when a wanted story is `planned` or `later`.
5. **Plant:**
   - Fill only what the storylines need, within the limits (2 lines, minimal
     staff).
   - Confirm names (site, lines, products) that feel like their plant.
6. **Preview and approval:**
   - Summarize the headline numbers, and show where each storyline appears and
     when.
   - Ask for approval. Never build without it.
7. **Demo guide:** produced from the chosen storylines' talk tracks with the
   real tag root, table prefix and times.

**File**: `examples/storylines/demo-guide.md` (new). A template filled from
the talk tracks: audience, storyline order, and for each step the tool, the
query or tag, and the expected observation.
</change>

<change component="Claude Code skill">
**File**: `skills/flow-data-simulator/SKILL.md`

- Replace the interview section with: "Follow `examples/storylines/interview.md`;
  propose storylines from `examples/storylines/storylines.json`".
- Add a step that writes the demo guide after build.
- Update the installed copy (`~/.claude/skills/flow-data-simulator`).
</change>

<change component="Tests">
**File**: `tests/IndustrialDataSim.Tests/PlantStorylineTests.cs` (new)

- **Library integrity:** the library loads; ids are unique; the fixed lists
  hold; every `available` entry's `requires` areas exist in its base.
- **Fragments build valid models:** for each `available` storyline,
  `Compose(base, [id])` passes `PlantModelLoader.Load`, and a 3-day engine run
  shows the planted story. For example, the storyline-1 failure mode appears
  in the failures, with a corrective ticket.
- **Pairs and conflicts:** every pair of `available` storylines on the same
  base composes, or reports `storyline.conflict`. A known conflicting pair is
  asserted.
- **Talk tracks name real things:** every placeholder resolves, and every named
  table or tag exists in the composed model's schema and tag list.
</change>

</changes-required>

<success-criteria>

<automated-verification>
- [x] `dotnet build IndustrialDataSim.slnx -c Release`
- [x] `dotnet test IndustrialDataSim.slnx -c Release` (all, including `PlantStorylineTests` and unchanged `PlantSnapshotTests`)
- [x] `examples/storylines/storylines.json` contains 15 entries: 3 available, 5 planned (3a, 3b, 5a, 5b, 5c), 7 later
</automated-verification>

<end-to-end-verification>
- [x] Compose storylines 1 + 2 into a model file; `plant validate` returns valid; `plant preview … 24` shows the planted labeler failure with its ticket
- [x] A fresh Claude Code session with the updated skill, given "food & beverage, plant manager and maintenance lead, worried about late orders", proposes storyline 1 first and says golden batch is "planned"
- [x] Every talk-track step for storyline 1 resolves to tags and tables that exist after `plant build` of a `PlanTest…` simulation (then remove it)
</end-to-end-verification>

</success-criteria>

<post-verification>
Commit: "plant: storyline library and need-first interview script". Proceed to
phase 2 and/or phase 5.
</post-verification>

</plan>
