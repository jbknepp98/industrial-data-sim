<plan title="Interview Front End for the Flow Data Simulator: Implementation Plan">

<overview>
A local web container, shipped with the Timebase Docker stack, where a Timebase
user is interviewed by Claude about what they want to show and to whom. The
answers become a validated plant model; the user approves a preview, and the
container builds and runs the plant.

The interview starts with a short needs analysis (audience, their costly
problems, the systems they run, constraints). It maps the answers to storylines
grounded in real Flow customer conversations (`customer-needs-analysis.md`).
It finishes with a demo guide that says what to show, in which tool and in
which order.

Claude is reached through Pulse's Gage proxy, so the user enters no new key:
the LLM provider the customer already approved for Gage is reused. The engine
gains the two story families the needs analysis asked for, each with variants:

- **Certification compliance:** technicians (3a) and operators (3b).
- **Golden batch drift:** a chemicals reactor (5a), beverage syrup batching
  feeding the filler (5b), and pharma under GxP (5c).
</overview>

<current-state-analysis>
The interview exists only as a Claude Code skill. The simulator is a CLI in
Docker, and every plant runs as its own container started by hand.

<key-discoveries>
**The skill and the simulator today**
- The interview script lives in `skills/flow-data-simulator/SKILL.md`. It needs
  Claude Code to run.
- `plant validate|preview|build|run|status|remove|connect-spec` in
  `src/IndustrialDataSim.Cli/PlantCommands.cs` are the operations the web tools
  wrap. The web container calls the same Core and Runtime code directly.
- The model loader is strict and gives actionable, per-field errors
  (`src/IndustrialDataSim.Core/Plant/PlantModelLoader.cs`), which suits Claude
  filling a model through tool calls and fixing what validation reports.
- Optional plant areas follow one pattern: an optional model section, extra
  tables, extra tags, and an optional connect-spec query. See Quality and
  Shipping across `PlantModel.cs`, `PlantSchema.cs`, `PlantSignals.cs`,
  `PlantEngine.cs` and `PlantConnectSpec.cs`. Certification and batch follow it.

**Engine guarantees new code must keep**
- `PlantSnapshotTests` pins the engine output with hashes. New optional
  sections must leave models without them byte-identical.
- Any change to the snapshot state layout must bump `PlantEngine.EngineVersion`.
  Stored snapshots of the old layout are then ignored once and the plant
  replays from its start.
- `PlantHost` and `PlantStore` (snapshots, resend, one advisory lock per
  simulation) already support running plants as background services in one
  process.

**Pulse and the Gage proxy**
- `HistorianClient` already gets Pulse tokens by client credentials, using the
  CA bundle (`src/IndustrialDataSim.Runtime/HistorianClient.cs:45`,
  `HistorianConnection.cs`). The Gage proxy uses the same tokens.
- Pulse exposes `POST /api/gage/proxy/v1/messages` (the Anthropic Messages wire
  format) and `GET /api/gage/models`. Any authenticated client may call them.
  Metering uses a per-client policy (Soft, Throttle with 429, or Block).
- The stack's seeded handle is `anthropic/claude`, currently mapped to Claude
  Sonnet 4.6. The provider key is write-only in Pulse and never readable.
  Saved API description: `pulse-openapi-2026-11-22.json`.

**Build constraints**
- The solution is .NET 10 with no web project yet. Adding ASP.NET Core and the
  official `Anthropic` NuGet package must pass the existing CI jobs: the
  dependency audit, the offline builds and four package RIDs.
</key-discoveries>
</current-state-analysis>

<desired-end-state>
A user with a running Timebase stack:

1. Starts the `flow-data-simulator-web` container, or adds it to compose, with
   a Pulse client ID and secret and the CA bundle.
2. Opens `https://localhost:<port>` and is interviewed. If Pulse has no usable
   model, the page says exactly which Pulse setting is missing and stops.
3. Sees storylines picked for their industry and audience, then a plant
   preview: Gantt chart, breakdowns, holds, batches and certifications.
4. Clicks **Approve and run**. The container admits the plant and runs it in
   the background: rows go to `flow_data_sim`, tags to the configured Dataset.
5. Receives the table names, tag root, Connect spec, Atlas starter and a
   one-page demo guide.

Plants survive container restarts through snapshots. Everything is verified
end to end in the live stack.
</desired-end-state>

<out-of-scope>
- A Flow-hosted service, Anthropic Managed Agents, or a direct Anthropic key
  (owner decision: reuse Gage's provider, and "explain and stop" when no model
  is reachable).
- An AI-free form fallback.
- Storylines 7 (MCC trip), 9, 10, 11 and 12, scrap logs and manual
  downtime-entry errors. They are listed in the library as "planned".
- Multi-site plants, more than 2 lines (one batch unit counts as a line), and
  login or multi-user access inside the web page. Access is the localhost port;
  the Pulse client is the identity.
- Sleep and power-cycle acceptance (deferred separately).
- Migrating the existing BluebirdBev plant into the web container. It keeps
  running as its own container until the owner chooses to move it.
</out-of-scope>

<implementation-approach>
Each piece is chosen to fit how Timebase customers actually buy and deploy.

- **All C#, one container.** ASP.NET Core minimal API inside the existing
  solution (`src/IndustrialDataSim.Web`) serves a plain HTML/JS chat page and
  calls the simulator's Core and Runtime directly.
- **Plants run inside the web container** (owner decision). A hosted service
  starts a `PlantHost` for each approved plant and restarts them when the
  container starts. There is no Docker socket.
- **Claude through Pulse.**
  - Use the official `Anthropic` C# SDK with its base URL set to the Pulse
    proxy, authenticating with the Pulse bearer token. Phase 5 proves the exact
    SDK settings against the live proxy before anything depends on them.
  - The model is the Gage handle, configurable, defaulting to
    `anthropic/claude`. The upstream model may be older than Opus 5.5, so the
    loop uses only portable features: tool use with `strict` schemas, no forced
    `tool_choice` and no prefill.
- **The interview is a tool loop the app owns.** Claude gets tools to:
  - read the storyline library and its fragments;
  - write the draft model;
  - validate it (field errors come back as the tool result);
  - build a preview summary.

  Building and running are never tools: only the user's **Approve** click
  starts them. Conversations and drafts are stored in `flow_data_sim`, in
  simulator-owned tables, so a browser refresh or container restart resumes
  the interview.
- **Need-first interview.** The system prompt and the storyline library come
  from the needs analysis. The library is data, not prompt text, so the Claude
  Code skill and the web container offer the same storylines.
- **Engine extensions follow the Quality/Shipping pattern:** optional sections,
  hash-pinned output for models without them, and an `EngineVersion` bump for
  the snapshot layout change. They are independent of the front-end phases;
  phases 2–4 and 5–7 can run in parallel.
</implementation-approach>

<phases>

<phase number="1" name="Storyline library and interview script" detail-plan="phase-1-plan.md">
<summary>The 15 needs-analysis storylines as versioned data (industry, roles,
KPIs, systems, engine support, model fragment, demo talk track), the interview
script, and the Claude Code skill updated to use the same library.</summary>
<files-affected>~6: examples/storylines/*.json, Core/Plant/PlantStorylines.cs, skills/flow-data-simulator/*, tests</files-affected>
<depends-on>None</depends-on>
<verification-summary>
- Every "available" storyline fragment, merged into the base template, passes `plant validate`
- The skill interview offers storylines from the library
</verification-summary>
</phase>

<phase number="2" name="Certification compliance in the engine" detail-plan="phase-2-plan.md">
<summary>Optional certification section: certificates with expiry, a lapse
incident, an assignment rule that can use an uncertified technician under
pressure, new compliance tables and query properties. Storylines 3a
(technicians closing work orders) and 3b (operators running lines or SKUs
without a current work-instruction certificate) become available.</summary>
<files-affected>~9: PlantModel, PlantModelLoader, PlantEngine, PlantSchema, PlantConnectSpec, PlantPreview, docs, tests</files-affected>
<depends-on>Phase 1 (library entry flips to available)</depends-on>
<verification-summary>
- Golden hashes unchanged; EngineVersion 2; resume-equivalence tests extended to a certification model
- Live: an uncertified closure appears in Postgres at the planted time
</verification-summary>
</phase>

<phase number="3" name="Batch area: recipes, phases, batch records, tags" detail-plan="phase-3-plan.md">
<summary>Optional batch section: one batch unit (counts as a line) runs
recipes as phase sequences with setpoint profiles. It writes batch and phase
records, and Temperature, Pressure, AgitatorSpeed, Level, Phase and BatchId
tags. Ties into maintenance failures and the snapshot state.</summary>
<files-affected>~9: same engine files plus PlantSignals and the preview page</files-affected>
<depends-on>Phase 2 (EngineVersion and snapshot layout sequencing)</depends-on>
<verification-summary>
- Batch tags follow phase setpoints; phase records tile each batch; snapshot resume identical
</verification-summary>
</phase>

<phase number="4" name="Golden batch drift and batch quality" detail-plan="phase-4-plan.md">
<summary>The golden profile per recipe, a deviation score per batch, a
planted drift (for example jacket fouling slows heating), cycle-time and yield
effects, lab results and holds tied to batches, connect-spec and Atlas
queries. Storylines 5a (chemicals
reactor), 5b (beverage syrup batching feeding the filler) and 5c (pharma, with
an audit trail) become available.</summary>
<files-affected>~7</files-affected>
<depends-on>Phase 3</depends-on>
<verification-summary>
- Drifted batches show longer heat phases, lower yield and holds; non-drifted batches stay within the golden band
</verification-summary>
</phase>

<phase number="5" name="Pulse Gage gateway client" detail-plan="phase-5-plan.md">
<summary>Shared Pulse token source (from HistorianClient), the Anthropic SDK
pointed at the Gage proxy, model discovery via /api/gage/models, and
plain-language diagnostics for every failure ("explain and stop").</summary>
<files-affected>~6: Runtime/Pulse*.cs, HistorianClient refactor, CLI `plant gage-check`, tests</files-affected>
<depends-on>None (can run in parallel with 2–4)</depends-on>
<verification-summary>
- `plant gage-check` gets a real reply through the live Pulse proxy; each misconfiguration yields its specific message
</verification-summary>
</phase>

<phase number="6" name="Interview service" detail-plan="phase-6-plan.md">
<summary>The tool loop: system prompt, library tools, draft-model, validate
and preview tools, and persisted conversations in flow_data_sim. A headless
API and a scripted-interview test with a fake model.</summary>
<files-affected>~8: src/IndustrialDataSim.Interview (or Web/Interview), store tables, tests</files-affected>
<depends-on>Phases 1 and 5</depends-on>
<verification-summary>
- A scripted fake-model interview produces a valid model; a live interview through Pulse reaches a valid preview
</verification-summary>
</phase>

<phase number="7" name="Web page, approval and in-container plant hosting" detail-plan="phase-7-plan.md">
<summary>ASP.NET Core host, the chat page with a preview panel (reusing the
plant-preview renderer), Approve and run, a hosted service running approved
plants, status, downloads (Connect spec, Atlas starter, demo guide), and
remove.</summary>
<files-affected>~10: src/IndustrialDataSim.Web/*, wwwroot/*, tests</files-affected>
<depends-on>Phase 6</depends-on>
<verification-summary>
- Browser: interview → preview → approve → status shows live; restarting the container resumes the plant from its snapshot
</verification-summary>
</phase>

<phase number="8" name="Packaging, docs and live end-to-end" detail-plan="phase-8-plan.md">
<summary>The web container image, a compose snippet, Pulse client and policy
setup steps, CI (dependency audit, RIDs), docs and skill updates, then a full
live run: two interviews (a beverage audience covering 1, 2 and 5b, and a chemicals
audience covering 5a, 3a and 3b)
to running plants verified against the engine.</summary>
<files-affected>~8: Dockerfile, deployment/, docs/, .github/workflows, skills/</files-affected>
<depends-on>Phases 2, 4 and 7</depends-on>
<verification-summary>
- CI green; both live plants verified (Postgres rows and Historian tags match the engine)
</verification-summary>
</phase>

</phases>

<overall-success-criteria>
When all phases are complete:
- [x] `dotnet build IndustrialDataSim.slnx -c Release` and `dotnet test IndustrialDataSim.slnx -c Release` pass, including the live Postgres tests (`FLOWSIM_TEST_PG` set)
- [x] CI passes on all jobs, including the dependency audit for ASP.NET Core and the `Anthropic` packages (run 44999587785 on the merge, run 44999977188 on `v2.0.0-rc.1`)
- [x] Golden engine hashes are unchanged for models without the new sections
- [x] With Pulse's Gage provider configured, a new user goes from an empty page to a running, verified plant without the CLI or Claude Code
- [x] With no Gage model reachable, the page names the missing Pulse setting and stops; nothing is written (WebTests.AGageStopExplainsTheFixAndAdmitsNothing; the image smoke test shows `gage.missing_setting` with the database untouched)
- [x] The demo guide names questions that work in the live stack (a Connect query and an Explorer trend) for the chosen storylines
</overall-success-criteria>

<execution-notes>
Each phase has its own detailed plan file.

- **Order:** run phases 1, then 2 → 3 → 4 and 5 → 6 → 7 (the two tracks can run
  in parallel), then 8. Use `/implement-plan docs/11212026_interview-front-end/phase-N-plan.md`.
- **After every phase:** build, run the full tests, end-to-end checks, commit.
- **Engine phases (2–4):** never change output for existing models. The golden
  hash test is the gate. Bump `EngineVersion` once per layout change.
- **Live checks:** use simulation names that start with `PlanTest`, and remove
  them afterwards. Never touch the BluebirdBev tables or other Datasets.
- **Running the dev build:** the dev image is built from source and is not a
  release. Any release needs its own version number and acceptance run.
</execution-notes>

<references>
- Needs analysis: `docs/11212026_interview-front-end/customer-needs-analysis.md`
- Pulse API description (saved): `docs/11212026_interview-front-end/pulse-openapi-2026-11-22.json`
- Plant docs: `docs/plant-simulation.md`, `docs/design-plant-simulation.md`
- Current interview: `skills/flow-data-simulator/SKILL.md`
- Area pattern to copy: Quality/Shipping in `src/IndustrialDataSim.Core/Plant/*`
- Pulse auth reference: `src/IndustrialDataSim.Runtime/HistorianClient.cs:45`
- Snapshot guarantees: `tests/IndustrialDataSim.Tests/PlantSnapshotTests.cs`
</references>

</plan>
