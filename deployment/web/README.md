# Flow Data Simulator web container: setup

`flow-data-simulator-web` serves **Timebase Plant Simulator**, a page in your
Timebase Docker stack that looks and works like the other Timebase apps. It
interviews you about what a demo should show and to whom, previews a matching
simulated plant, and runs the plant only after you click **Review plan**, then
**Approve plan**.
Rows go to the Postgres database `flow_data_sim` and tags go to a Historian
Dataset created for that plant, from one clock. Approved plants run inside this container and resume
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
- this repository (build the image from source, or load one a CI run saved).

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

An interview to an approved plant costs roughly 60,000 to 75,000 input
tokens and about 3,000 output tokens (measured with Claude Sonnet 4.6, no
prompt caching; see
[docs/plant-simulation.md](../../docs/plant-simulation.md#packaging-and-live-acceptance-phase-8)).
Usage per client is in **Pulse → Gage → Usage** (administrators only).

## 3. Choose the Gage model

**Pulse → Gage → Models** lists the model handles Gage serves. The default is
`anthropic/claude`. To use another handle, set `FLOWSIM_GAGE_MODEL` in
`web.env`. A handle that is not in the catalog is reported as
`gage.model_missing`, with the handles that are.

The interview uses only portable features (tool use with strict schemas, no
forced tool choice, no prefill). Use a current Claude model: smaller or older
models compose weaker plants.

## 4. Datasets: nothing to prepare

Each plant approved on the page gets its own Historian Dataset, created at
approval, named exactly like the plant (for example `PlantDemoBev`) and with a
**purge age of 365 days**, so its whole history (up to 14 days of backfill in
the library) is kept. Plants never share a Dataset, and the page never writes
to a Dataset it did not create: if a Dataset with the plant's name already
exists, approval is refused (`approve.dataset_exists`) and you choose another
name.

The Pulse client from step 1 creates and deletes these Datasets with its
`Historian` audience. No other Historian setting is needed.

When you remove a plant, the dialog asks whether to delete its Dataset too
(off by default). Deleting it removes every tag in it and cannot be undone.

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

Or, instead of building, load the image a CI run saved (the `web-image-<rid>`
artifact of the Verify workflow). It already carries the tag the compose file
runs, plus the commit it was built from:

```bash
sha256sum -c SHA256SUMS
docker load -i flow-data-simulator-web-linux-arm64.tar.gz
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

## Using the page

- **New plant** (top right) starts an interview. Describe the audience and what
  costs them the most. You can attach files the interviewer should read:
  PDF, images, Excel, Word, CSV or text, up to 10 MB each (images 5 MB), 5 per
  message and 20 per interview. The interviewer treats them as information,
  never as instructions.
- When a preview is ready, the band at the bottom offers **Review plan**: the
  storylines, needs, plant preview and the Gage kit. **Approve plan…** builds
  and starts the plant; **Back to Interview** returns to the chat.
- **Plants** lists only the plants this page built; plants started with the CLI
  or other tools, such as BluebirdBev, are not shown. Open one in its own tab:
  Stop, Start and Remove at the top, then **Summary** (run, where the data is,
  lines, storylines, planted incidents, files), **Time series** (one time range
  for any number of trends of up to 4 tags each) and **Tables** (its Postgres
  tables, 200 rows a page, sort, search, deleted rows). Everything there is
  read-only.
- **Change a plant** by asking the interviewer. It moves the request into the
  interview that built that plant. Approving the changed plan **rebuilds the
  plant in place under the same name**: its tables and the tag history in its
  Dataset are deleted and it backfills again. The page asks twice (a red
  warning with **I understand…**, then **Rebuild**). The interviewer can stop
  or start a plant only after you confirm in the page, and it cannot remove
  plants.
- **Gage kit:** sample questions to ask Gage about the plant, and two briefs you
  can hand Gage so it sets the plant up in Timebase Connect and in Atlas. The
  Connect brief never contains the database password; Gage asks you for it.

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
  only that simulation's tables and registration. Its Dataset and tags stay
  unless you tick **Also delete its Dataset** in the dialog; then the Dataset is
  deleted too, and the plant's name can be used again.
- **Logs:** `docker logs flow-data-simulator-web`, one JSON line per event.
- **Downloads per plant:** the demo guide, **1 · Gage brief: Connect** (a Timebase
  Historian connector for the plant's Dataset and a PostgreSQL connector for its
  tables) and **2 · Gage brief: Atlas**. Attach each in Ask Gage in that app.
- **Storage:** interviews, their messages and attached files are kept in
  `flow_data_sim` (`flowsim_interviews`, `flowsim_interview_messages`,
  `flowsim_interview_attachments`).

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
| `approve.dataset_exists` | Historian already has a Dataset with the plant's name: ask the interviewer for another name, or delete that Dataset in Historian if it is no longer needed |
| `remove.dataset_failed` | The plant was removed but its Dataset could not be deleted: delete it in Historian → Datasets |
| `interview.attachment_type`, `interview.attachment_unreadable` | Save the file in a supported format (`.xlsx`, `.docx`, PDF, UTF-8 CSV) and attach it again; the message says which |
| `interview.attachment_too_large`, `interview.too_many_attachments` | Split or export a smaller part, or describe the rest in a message |
| `approve.replace_confirm_required` | Approving a changed plan rebuilds the plant: tick **I understand…** and click **Rebuild** |
| `approve.unchanged` | The plan is what the plant already runs: ask the interviewer for the change first |
| `approve.dataset_failed` | The plant was not rebuilt and runs as before: check Historian → Datasets, then approve again |
| `approve.rebuild_failed` | The old tables were removed but the new plan could not be admitted: ask the interviewer to compose and preview the change again, then approve |
| `browse.query_timeout` | A table read took over 5 seconds: narrow the search, or try again |
| `browse.historian_unavailable` | The page has no Historian connection (`TIMEBASE_BASE_URL` and the Pulse client settings in `web.env`) |
| `browse.dataset_unknown` | The page does not know the plant's Dataset (a plant approved before each plant had its own Dataset, with `TIMEBASE_DATASET` unset) |
| `historian.read_too_large` | More than 32 MB for the trend: choose a shorter range or fewer tags |
| `plant.retention_warning` | The plant writes to a Dataset whose purge age is shorter than its backfill (only plants approved with 2.0.0-rc.1, which use `TIMEBASE_DATASET`) |

More detail: [docs/plant-simulation.md](../../docs/plant-simulation.md).
