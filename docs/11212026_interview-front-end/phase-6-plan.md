<plan title="Phase 6: Interview service">

<overview>
Phase 6 of "Interview Front End for the Flow Data Simulator". The headless
interview: a tool loop that the app owns. Claude runs the need-first interview,
reads the storyline library, composes and validates a plant model, and
prepares a preview. Conversations persist. There is no web page yet; that is
phase 7.
</overview>

<context>
Assumes phase 1 (library and script) and phase 5 (`GageClient`) are complete.
Phases 2–4 may or may not be done: the library's `engine` flags tell the
interview what is available.
</context>

<changes-required>

<change component="Project">
**File**: `src/IndustrialDataSim.Interview/` (new class library, referenced by
the web project in phase 7)

Keep it out of Core: Core stays free of LLM dependencies.
</change>

<change component="System prompt">
**File**: `Interview/InterviewPrompt.cs`

- Built from `examples/storylines/interview.md`, plus the library summary (ids,
  titles, pitches, industries, roles, KPIs, engine status), plus the plant
  limits.
- It is stable text, so it caches well. The current date and per-session facts
  go in the first user turn, never in the system prompt.
- Written for the model that sits behind Gage, which may not be the newest:
  plain instructions, no prefill, and strict tool schemas.
</change>

<change component="Tools (strict JSON schemas, `strict: true`)">
**File**: `Interview/InterviewTools.cs`

- **`record_audience`:** industry, roles, KPIs, pains, systems, constraints.
  Saved as the session's needs profile; it feeds the demo guide.
- **`find_storylines`:** given the profile, returns ranked storylines with
  their engine status and the reasons for the ranking. The ranking is
  deterministic code (industry, role and KPI overlap), not the model's guess.
- **`compose_model`:** takes storyline ids, overrides (simulation name, site,
  line and product names, people, backfill), and the base. It returns
  validation errors in the loader's field-path form, or "valid" plus a short
  model summary. The draft is stored.
- **`preview_plant`:** runs the engine over the preview window. It returns the
  headline numbers and where each storyline appears (times, tickets, holds,
  batches, uncertified assignments) so the model can explain it. The full
  preview JSON is stored for the page.
- **No build or run tool.** Approval is the user's click (phase 7). The prompt
  says so, and a test asserts that the tool list contains no build or run tool.
</change>

<change component="Loop">
**File**: `Interview/InterviewSession.cs`

- A manual loop on `GageClient.CreateMessage`: send, run tool calls, return
  all results in one user message, and repeat until `end_turn`.
- **Limits:**
  - at most 12 tool rounds per user turn;
  - `max_tokens` 16000 (non-streaming) or larger when streaming;
  - tool inputs are validated before they run (with `strict`, plus our own
    checks);
  - a malformed input returns an `is_error` tool result rather than crashing.
- **Stop reasons:**
  - `max_tokens`: continue once, then surface the problem;
  - `refusal`: show a plain message;
  - Gage diagnostics (phase 5) end the turn with an "explain and stop" event.
- **Events for the page:** assistant text, tool activity ("Checking the plant
  model…"), preview-ready, errors.
</change>

<change component="Persistence">
**File**: `Interview/InterviewStore.cs`

Simulator-owned tables, created on first use like `flowsim_snapshots`, never
under a simulation prefix:

- `flowsim_interviews`: id, created_utc, updated_utc, status, needs_json,
  draft_model_json, preview_json, approved_simulation.
- `flowsim_interview_messages`: interview_id, seq, role, content_json, usage
  tokens.

The message history is replayed exactly as stored, append-only, so prompt
caching and thinking blocks survive.
</change>

<change component="Tests">
- **Scripted interview with a fake `GageClient`:** a beverage audience leads to
  `record_audience`, `find_storylines` (storyline 1 ranked first),
  `compose_model` (an invalid first try that returns field errors), a fixed
  `compose_model`, then `preview_plant`. The session ends with a valid draft
  and a preview.
- **Robustness:** tool-input validation and `is_error` handling; the
  round-limit cutoff; resuming after a restart reloads the session and
  continues.
- **Ranking:** `find_storylines` is deterministic for a set of profiles,
  including "pharma batch, process engineer", which leads with storyline 5
  (marked planned until phase 4 is done).
- **Live (gated `FLOWSIM_TEST_GAGE=1`):** a 3-turn scripted user against the
  real Gage proxy reaches a valid preview. Record the token usage.
</change>

</changes-required>

<success-criteria>
<automated-verification>
- [x] Build and full tests pass; the fake-model interview test is green (Release 1044/1044 with `FLOWSIM_TEST_PG` set, November 22, 2026; `InterviewTests` 20, the live one gated by `FLOWSIM_TEST_GAGE=1`)
- [x] No build or run tool is exposed (asserted by `NoBuildOrRunToolIsExposed`, which also checks the schemas are strict)
</automated-verification>
<end-to-end-verification>
- [x] A small console harness (`tools/IndustrialDataSim.InterviewProbe`) runs a live interview through Gage from the terminal, playing "maintenance manager at a bottler, late orders": the conversation reaches a valid preview, and the cost per interview is recorded in tokens. Passed through Gage (`anthropic/claude`) in the simulator image on the Timebase network: `PlanTestBottler`, valid draft, 2.0 MB preview. 3 turns used 87,767 input and 3,382 output tokens (9 model calls). Pulse reported no cache reads, because requests carry no `cache_control`
- [x] Kill the probe mid-interview and restart it: the session resumes at the last message. The container was SIGKILLed during turn 2's `compose_model`, leaving status `in_turn` with 7 messages stored. `--resume` finished the turn (preview), skipped the 2 script lines already sent and reached a valid preview
- [x] Ask for an unsupported story ("energy spikes"): the interviewer says it's not available yet and offers the closest available one. Passed twice live. `energy-per-unit-spike` has `closest: null`, so the tool offers the best available match for the audience and says nothing close exists. The model said so and offered "Labeler jams make orders late"
</end-to-end-verification>
</success-criteria>

<post-verification>
Commit: "interview: need-first tool loop over Gage with persisted sessions". Proceed to phase 7.
</post-verification>

</plan>
