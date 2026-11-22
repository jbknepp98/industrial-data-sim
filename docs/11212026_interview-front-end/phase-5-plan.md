<plan title="Phase 5: Pulse Gage gateway client">

<overview>
Phase 5 of "Interview Front End for the Flow Data Simulator". Reaches Claude
through Pulse's Gage proxy with the official Anthropic C# SDK, so the user
needs no separate key. When no model is reachable, it explains exactly what to
fix and stops (owner decision).
</overview>

<context>
Independent of phases 2–4. What the earlier research established:

- Pulse exposes `POST /api/gage/proxy/v1/messages` (the Anthropic Messages wire
  format) and `GET /api/gage/models`, with any authenticated client allowed.
  Tokens come from `POST /auth/token` (client credentials). The saved API
  description is `pulse-openapi-2026-11-22.json`.
- The model field takes a Gage handle (seeded: `anthropic/claude`, currently
  Claude Sonnet 4.6). Pulse swaps in the real model and forwards the body.
- `HistorianClient` already implements the token request with the CA bundle
  (`src/IndustrialDataSim.Runtime/HistorianClient.cs:45`).
</context>

<changes-required>

<change component="Shared Pulse token source">
**Files**: `src/IndustrialDataSim.Runtime/PulseTokenSource.cs` (new),
`HistorianClient.cs`, `HistorianConnection.cs`

- Extract the token request and caching (refresh before expiry) from
  `HistorianClient` into `PulseTokenSource`, using the same settings
  (`TIMEBASE_PULSE_URL`, client ID and secret, `TIMEBASE_CA_BUNDLE`).
- `HistorianClient` uses it. No behavior change, and the existing tests must
  pass unchanged.
- The token is never logged.
</change>

<change component="Gage client">
**File**: `src/IndustrialDataSim.Runtime/GageClient.cs` (new). Add the
`Anthropic` NuGet package to Runtime, pinned to an exact version.

1. **Spike first (half a day): prove the SDK settings against the live proxy**
   and record them in the file header:
   - base URL `https://<pulse>/api/gage/proxy`, so that the SDK's `/v1/messages`
     lands on the proxy path;
   - the Pulse token sent as a Bearer token;
   - whether Pulse needs or tolerates the SDK's `anthropic-version` and beta
     headers;
   - TLS trust through a handler that adds the Pulse CA.

   Find the exact client properties (base URL, auth token, custom
   HttpClient/handler) with `strings` on the installed `Anthropic.dll`. Fix
   names from compiler errors; don't guess and don't build reflection probes.
   **If the SDK cannot target the proxy cleanly**, stop and report to the
   owner with the evidence before choosing a fallback. Do not hand-roll HTTP
   silently.
2. **`ListModels()`** calls `GET /api/gage/models` and returns the handles. The
   configured handle (`FLOWSIM_GAGE_MODEL`, default `anthropic/claude`) must be
   among them.
3. **`CreateMessage(...)`** is a thin pass-through used by the interview loop:
   tools, system, messages and `max_tokens`. It streams for long replies,
   always sends `max_tokens` (the proxy only fills a default), and uses no
   forced `tool_choice` and no prefill.
</change>

<change component="Diagnostics: explain and stop">
**File**: `GageClient.cs` (`Check()`), plus `plant gage-check` in `PlantCommands.cs`

Map each failure to a `RuntimeFailure` code and an actionable message:

| Code | Condition | Message tells the user to |
| --- | --- | --- |
| `gage.pulse_unreachable` | DNS, TCP or TLS failure | check `TIMEBASE_PULSE_URL`, the network and the CA bundle |
| `gage.client_rejected` | 401 from `/auth/token` | create or fix the Pulse client (Pulse → Clients) and its secret |
| `gage.no_models` | empty catalog | add a provider in Pulse → Gage → Providers |
| `gage.model_missing` | the handle is not in the catalog | list the available handles and set `FLOWSIM_GAGE_MODEL` |
| `gage.no_key` | the provider has no key | set the key in Pulse → Gage → Providers |
| `gage.blocked` | 403 or a Block policy | ask the Pulse admin to allow the `DataSim` client |
| `gage.throttled` | 429 | the message includes the policy window; the web page shows it and offers a retry |

`plant gage-check` runs the full chain: token, model catalog, then a
one-sentence test message, and prints the model handle and the token usage.
</change>

<change component="Tests">
- **Diagnostics:** unit tests with a fake `HttpMessageHandler` cover each
  diagnostic code, the token caching and refresh, and the fact that no secret
  appears in any error or log line.
- **Request shape:** a test asserts that `CreateMessage` sends the handle as
  `model`, always sets `max_tokens`, and never sets forced `tool_choice`.
- **Live (gated):** with `FLOWSIM_TEST_GAGE=1` and the private env file, a live
  `Check()` against the stack succeeds.
</change>

</changes-required>

<success-criteria>
<automated-verification>
- [ ] Build and full tests pass; the HistorianClient tests are unchanged
- [ ] The dependency audit (CI) accepts the `Anthropic` package
</automated-verification>
<end-to-end-verification>
- [ ] One-time setup (owner approves): create Pulse client `DataSim` with policy Soft; put its ID and secret in `.tools/private/container.env` (never printed)
- [ ] `plant gage-check` in the dev container on the Timebase network prints the model handle and a reply
- [ ] Wrong secret gives `gage.client_rejected`; `FLOWSIM_GAGE_MODEL=nope/x` gives `gage.model_missing` listing the real handles; Pulse stopped gives `gage.pulse_unreachable`
- [ ] Pulse usage (`/api/gage/usage`) shows the test call under the `DataSim` client
</end-to-end-verification>
</success-criteria>

<post-verification>
Commit: "runtime: Gage proxy client with actionable diagnostics". Proceed to phase 6.
</post-verification>

</plan>
