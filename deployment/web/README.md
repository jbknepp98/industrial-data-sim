# Flow Data Simulator web container: setup

`flow-data-simulator-web` is a page in your Timebase Docker stack. It
interviews you about what a demo should show and to whom, previews a matching
simulated plant, and runs the plant only after you click **Approve and run**.
Rows go to the Postgres database `flow_data_sim` and tags go to a Historian
Dataset, from one clock. Approved plants run inside this container and resume
after a restart.

The interview reaches Claude through Pulse's Gage proxy, so you enter no model
key: it uses the provider your Pulse administrator already set up for Gage. If
no model is reachable, the page says which Pulse setting to fix and stops.

**The page has no login.** Anyone who can open the port can start interviews
(which spend Gage tokens) and build or remove plants. Publish it only on the
host's loopback (`127.0.0.1`), as below, or on a network you trust.

You need:

- a running Timebase 2.0 stack (Pulse, Historian; Explorer and Connect for the
  demo) and its Docker network name (`docker network ls`, for example
  `timebase-2-0-beta_default`);
- the Pulse root CA certificate as a PEM file;
- a Pulse administrator for steps 1–3;
- this repository (the image is built from source until a release is published).

Commands run from the repository root. Keep the private files (`web.env`,
`postgres.env`, the CA) outside the repository, for example in
`~/flow-data-simulator/`, with mode 600.

## 1. Create the Pulse client

In **Pulse → Clients**, create a client credentials client named `DataSim`
(any name works; it goes in `TIMEBASE_CLIENT_ID`). Copy its secret once.

Set its **Allowed Audiences** to both:

- `Historian`, so approved plants can write tags;
- `Pulse`, so the interview can call Gage. Without it, Pulse still issues a
  token, but Gage rejects it ("The audience '(null)' is invalid"), and
  `gage-check` reports `gage.client_rejected`.

## 2. Give the client a Soft policy

In **Pulse → Policy → Client**, add a **Soft** policy for `DataSim`. Soft
meters the interview's model calls but never refuses them. A Throttle policy
can stop an interview mid-turn (`gage.throttled`, HTTP 429); Block stops every
call (`gage.blocked`).

An interview costs roughly 65,000 to 90,000 input tokens and about 3,000
output tokens (measured with Claude Sonnet 4.6; see
[docs/plant-simulation.md](../../docs/plant-simulation.md#the-web-container)).
Usage per client is in **Pulse → Gage → Usage** (administrators only).

## 3. Choose the Gage model

**Pulse → Gage → Models** lists the model handles Gage serves. The default is
`anthropic/claude`. To use another handle, set `FLOWSIM_GAGE_MODEL` in
`web.env`. A handle that is not in the catalog is reported as
`gage.model_missing`, with the handles that are.

The interview uses only portable features (tool use with strict schemas, no
forced tool choice, no prefill). Use a current Claude model: smaller or older
models compose weaker plants.

## 4. Check the Dataset's purge age

Plants backfill history before they go live: 6 days for the library's base
plants (`timeline.backfill`). Historian drops points older than the Dataset's
**purge age**, so it must be longer than the backfill. Pick a Dataset just for
simulated data, for example `Test`, then in **Historian → Datasets → Test**
check the purge age (7 days or more for a 6-day backfill; 0 keeps everything).

If the backfill starts before the purge age, the plant still runs and logs
`plant.retention_warning`. Historian drops the oldest points, while Postgres
keeps every row, so trends and tables no longer agree for those days.

## 5. Write the env files

```bash
mkdir -p ~/flow-data-simulator/config ~/flow-data-simulator/certs
cp deployment/web/web.env.example ~/flow-data-simulator/config/web.env
chmod 600 ~/flow-data-simulator/config/web.env
cp /path/to/pulse-root-ca.pem ~/flow-data-simulator/certs/pulse-root-ca.pem
```

Edit `web.env`:

- the client ID and secret from step 1;
- `TIMEBASE_PULSE_URL` and `TIMEBASE_BASE_URL`, the Pulse and Historian
  addresses on the stack network (`https://pulse:4542`,
  `https://historian:4512` for the default compose service names);
- the Dataset from step 4;
- `FLOWSIM_PG_CONNECTION`, the `flowsim_writer` connection string.

**No `flow_data_sim` database yet?** Also create `postgres.env` from
`postgres.env.example` (pick three passwords; the writer password goes into
`FLOWSIM_PG_CONNECTION` too). The compose file starts `flow-data-sim-pg` and
bootstraps it on first start. If `flow-data-sim-pg` already runs, skip this
and the `--profile postgres` flag below.

## 6. Build and start

```bash
export FLOWSIM_WEB_ENV=~/flow-data-simulator/config/web.env
export FLOWSIM_PG_ENV=~/flow-data-simulator/config/postgres.env
export FLOWSIM_CERTS=~/flow-data-simulator/certs
export TIMEBASE_NETWORK=timebase-2-0-beta_default
```

```bash
docker compose -f deployment/web/compose.snippet.yml build
```

Run `gage-check` before you start the page (it is the same check the page runs
at startup):

```bash
docker compose -f deployment/web/compose.snippet.yml run --rm --no-deps --entrypoint /opt/datasim/app/IndustrialDataSim.Cli flow-data-simulator-web plant gage-check
```

It gets a Pulse token, reads the model catalog, sends one short test message
and prints the model handle, the reply and the token usage. If it stops, its
message names the Pulse setting to fix; rerun it after the fix.

Then start the page, and the database if you need it:

```bash
docker compose -f deployment/web/compose.snippet.yml --profile postgres up -d
```

Open <http://localhost:8480>. `FLOWSIM_WEB_PORT` changes the host port. The
container is healthy when the page answers; `GET /api/health` shows the
database and Gage status.

To open it by another host name (for example through an SSH tunnel), add that
name to `FLOWSIM_WEB_ALLOWED_HOSTS` in `web.env`.

## Operating it

- **Stop:** `docker compose -f deployment/web/compose.snippet.yml stop`, or
  `docker stop flow-data-simulator-web`. Both send SIGINT and wait up to 180 s,
  so each plant finishes its in-flight Historian batch and saves its
  snapshot. Never use `docker kill`: the next start would resend the
  interrupted batch (harmless, but slower).
- **Restart:** plants approved on the page start again and resume from their
  snapshots. A plant that another container already runs (for example one
  started with the CLI) shows **running elsewhere** and is never written from
  here.
- **Remove a plant:** use **Remove** on the page. It stops the plant and drops
  only that simulation's tables and registration. Historian keeps its tags
  until the purge age.
- **Logs:** `docker logs flow-data-simulator-web`, one JSON line per event.
- **Downloads per plant:** the Connect mapping spec, the Atlas starter and the
  demo guide.

## Troubleshooting

| Seen | Fix |
| --- | --- |
| `gage.client_rejected` | Wrong client ID or secret, or `Pulse` missing from the client's allowed audiences (step 1) |
| `gage.model_missing` | Set `FLOWSIM_GAGE_MODEL` to a handle from the message (step 3) |
| `gage.no_models`, `gage.no_key` | Pulse → Gage → Providers has no provider or no valid key: ask the Pulse administrator |
| `gage.throttled`, `gage.blocked` | The client's policy is Throttle or Block: use Soft (step 2) |
| `gage.pulse_unreachable` | `TIMEBASE_PULSE_URL`, the network name or the CA file is wrong |
| `web.database_unconfigured`, database not `ok` in `/api/health` | `FLOWSIM_PG_CONNECTION` is missing or wrong, or `flow-data-sim-pg` is not on the same network |
| Page answers 421 | Open it as `http://localhost:8480`, or add the host name to `FLOWSIM_WEB_ALLOWED_HOSTS` |
| `plant.retention_warning` | The Dataset's purge age is shorter than the backfill (step 4) |

More detail: [docs/plant-simulation.md](../../docs/plant-simulation.md).
