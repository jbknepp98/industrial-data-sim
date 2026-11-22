<plan title="Phase 7: Web page, approval and in-container plant hosting">

<overview>
Phase 7 of "Interview Front End for the Flow Data Simulator". The user-facing
container:

- an ASP.NET Core host serving the chat page and a preview panel;
- an **Approve and run** action that only the user can trigger;
- a hosted service that runs every approved plant inside this container (owner
  decision: no Docker socket);
- status, downloads and remove.
</overview>

<context>
Assumes phase 6 (`InterviewSession`) is complete. `PlantStore`, `PlantHost`
and snapshots already support running several plants in one process, with one
advisory lock per simulation.
</context>

<changes-required>

<change component="Web host">
**Files**: `src/IndustrialDataSim.Web/` (new ASP.NET Core minimal API project).
Configuration is environment variables only, matching the CLI.

Endpoints:

| Endpoint | Purpose |
| --- | --- |
| `GET /` | The page |
| `POST /api/interviews` | Start an interview |
| `GET /api/interviews/{id}` | History, draft and preview |
| `POST /api/interviews/{id}/messages` | Send a user message; the reply streams back as server-sent events |
| `POST /api/interviews/{id}/approve` | Body `{ "confirm": true }`. Admits the plant (`PlantStore.Admit`) with the anchor fixed now, registers it for hosting, and returns the table prefix and tag root |
| `GET /api/plants` | Status, as in `plant status`, plus live/backfilling/error per plant |
| `POST /api/plants/{name}/stop` and `POST /api/plants/{name}/start` | Stop or start a hosted plant |
| `DELETE /api/plants/{name}?confirm=true` | Stop the plant, then `PlantStore.Remove`. Only for simulations this container admitted (recorded in `flowsim_interviews.approved_simulation`) |
| `GET /api/plants/{name}/connect-spec`, `atlas-starter`, `demo-guide` | Downloads |
| `GET /api/health` | Includes the Gage check result |

- **Binding:** by default the host binds to the port mapped by Docker. The docs
  state that the page has no login, so it must not be exposed beyond the
  host's trusted network.
- **Request safety:** request size limits; JSON-only content types;
  anti-forgery checks on the state-changing POSTs (the `Origin` header must be
  the page's own).
</change>

<change component="Hosted plants">
**File**: `Web/PlantHostingService.cs`

- **Startup:** at container start, read the registry and start a `PlantHost`
  for each plant admitted by this container with hosting enabled. Each host
  takes the per-simulation advisory lock.
- **Plants already running elsewhere:** a plant held by another process (for
  example the existing BluebirdBev container) shows "running elsewhere" and is
  not touched.
- **Shutdown:** on graceful shutdown (SIGINT or SIGTERM), cancel all hosts and
  let their in-flight batches finish. The stop timeout in the docs is at least
  120 s.
- **Failures:** a host failure is reported on `/api/plants`, with the error
  code and message, and retried with backoff. Other plants keep running.
- **Log lines:** each host's events (`plant.live`, `snapshot_resumed` …) are
  written as JSON lines tagged with the simulation name.
</change>

<change component="Page">
**Files**: `Web/wwwroot/index.html`, `app.js`, `styles.css` (plain HTML and
JS; no framework, no CDN, so it works air-gapped)

- **Left pane, chat:** streaming text, tool-activity chips, and an "Explain and
  stop" banner for Gage errors showing the exact fix.
- **Right pane:**
  - storyline cards with their engine status;
  - the plant preview, reusing the renderer from
    `tools/plant-preview/index.html`, moved into a shared script;
  - the needs profile.
- **Approve and run:** a confirmation step in the page itself that shows the
  name, backfill, Dataset and tables. After approval the page shows live
  status, the downloads and the demo guide.
- **Plants tab:** status, stop/start, and remove with an in-page confirmation.
- **Look:** Flow design tokens, light and dark, and usable at laptop widths.
</change>

<change component="Demo guide">
**File**: `Web/DemoGuide.cs`

Fills `examples/storylines/demo-guide.md` from:
- the needs profile (audience, KPIs);
- the chosen storylines' talk tracks, with the real tag root, table prefix and
  event times taken from the preview;
- each storyline's "moment": for example "LB202 vibration starts rising at
  14:10 and the ticket opens at 22:15".

Output is Markdown and HTML.
</change>

<change component="Tests">
- **Endpoint tests (WebApplicationFactory, fake Gage):**
  - an interview runs through to a preview;
  - approve without `confirm` returns 400;
  - approve admits and starts the plant (live PG gated);
  - remove refuses simulations this container did not admit.
- **Hosting service:**
  - two plants run concurrently;
  - a host failure isolates to that plant;
  - restart resumes from snapshots;
  - "running elsewhere" is detected through the advisory lock.
- **Demo guide:** every placeholder resolves for storylines 1 and 2.
</change>

</changes-required>

<success-criteria>
<automated-verification>
- [x] Build and full tests (Release, live PG) pass
</automated-verification>
<end-to-end-verification>
- [x] Run the web project locally against the live stack; in the browser pane, complete an interview as a "bottler, maintenance lead, late orders" user. The preview shows the labeler story. Approve. Status reaches live, and the demo guide lists real tags and times
- [x] Verify the approved plant: Postgres rows and Historian tags match the engine (verify script, 0 mismatches)
- [x] Restart the web process: the plant logs `snapshot_resumed` and keeps delivering
- [x] With Pulse's `DataSim` client disabled: the page shows the `gage.blocked` banner with the fix, and no plant is admitted
- [x] BluebirdBev shows "running elsewhere" and is untouched
- [x] Remove the test plant from the page; its tables are gone, other tables untouched
</end-to-end-verification>
</success-criteria>

<verification-notes>
Verified November 22, 2026 (branch feature/phase-7-web). Full Release suite
1088/1088 with FLOWSIM_TEST_PG. Live: the published linux-arm64 build ran in
the dev simulator image on the stack network (datasim.env, /certs). Interview
"bottler, maintenance lead, late orders" → labeler preview → Approve →
PlanTestWebBottler live in 6 s; demo guide with real tags and times and the
unmet golden-batch ask. Engine check: 141,821 tag points and 554 rows (23
tables, 4,515 cells), 0 mismatches, also after a restart that logged
plant.snapshot_resumed. Gage blocked was simulated in a separate test
container (wrong FLOWSIM_GAGE_MODEL → gage.model_missing; wrong client secret
→ gage.client_rejected), not by changing Pulse: banner with the fix, composer
disabled, a forced send stopped, nothing admitted. A real gage.blocked needs a
Block usage policy in Pulse and is covered by the endpoint test. BluebirdBev
showed running elsewhere and was untouched. Removal from the page dropped 23
tables; the other 28 tables were unchanged.
</verification-notes>

<post-verification>
Commit: "web: interview page, approval, in-container plant hosting, demo guide". Proceed to phase 8.
</post-verification>

</plan>
