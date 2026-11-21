# Installation and operation

Release **1.0.1** (source `192aaab`): the 1.0.0 program plus a native
`linux-arm64` package. 1.0.0 (`cdf0681`) was a version-only rebuild of the
accepted `1.0.0-rc.2`. The supported deployment is Docker, using `linux-arm64` on
ARM64 hosts such as Apple Silicon, or `linux-x64` on x64 hosts. Packages are self-contained:
.NET is bundled, and neither a .NET SDK nor Python is needed for direct CLI use.
The optional startup supervisor requires Python 3.12 or later (standard library
only). See compatibility.md for the tested OS/CPU matrix. Do not treat an image
tag alone as a compatibility guarantee.

## Install without touching existing state

1. Obtain the RID-specific ZIP and its SHA-256 receipt from the same reviewed CI
   run. Authenticate the run/commit through the repository; checksums alone do
   not authenticate a download. Compare the ZIP with the separately obtained
   receipt (`sha256sum`, macOS `shasum -a 256`, or PowerShell `Get-FileHash`).
2. Extract to a new versioned local directory. On Unix, set executable permission
   on `app/IndustrialDataSim.Cli` (`chmod u+x`). ZIP extractors differ in whether
   they retain that bit. Keep native libraries alongside the executable. Do not
   copy only the apphost. Do not use a network/share/synchronized state directory.
3. Restrict package modification to its owner/administrator. Keep a separate
   private state/config directory (Unix mode 0700, environment/config files 0600;
   on Windows restrict ACLs to the dedicated account and administrators). Never
   run the simulator as root/administrator. Same-user processes are trusted;
   SQLite, named pipes and environment variables are not a security boundary
   against that account or a machine administrator.
4. Run `app/IndustrialDataSim.Cli validate-simulation examples/constant-simulation.json`
   (`.exe` on Windows). This requires no credentials. Native-loader failures
   occur before application diagnostics: check RID/CPU and OS dependencies, not
   TLS settings. Linux requires the usual .NET native OS libraries (glibc,
   OpenSSL, ICU, zlib, C++ runtime); bundled .NET does not mean a static binary.
5. Keep production environment settings in a private deployment file or managed
   process environment. Required names: TIMEBASE_PROFILE, TIMEBASE_BASE_URL,
   TIMEBASE_PULSE_URL, TIMEBASE_CLIENT_ID, TIMEBASE_CLIENT_SECRET, TIMEBASE_AUDIENCE;
   optional TIMEBASE_CA_BUNDLE points to a trusted PEM root bundle. HTTPS origins
   must have no path/query/credentials. Never put secrets in model JSON, arguments,
   startup templates, SQLite or issue reports. The CLI does not load `.env` itself.
6. With explicitly selected credentials and model, admit once using
   `app/IndustrialDataSim.Cli production start /absolute/state/production.sqlite /absolute/model.json`.
   This contacts the selected Historian. Inspect the result before running
   `production-host run /absolute/state/production.sqlite`. Admission is not a
   startup action; never re-admit on restart. Match the model's connection profile.

The source tool `scripts/verify_package.py <zip>` verifies a fresh extraction,
file hashes, native SQLite, ownership refusal, graceful stop, restart and synthetic
completion with credential variables removed. It does not publish to Historian.

## One startup and restart owner

Templates in deployment are **examples**, never installed by the build or test.
Copy host.example.json to the private directory; replace all paths. Windows uses
an `.exe` path. Set `maximumRestarts: 0` when an OS manager owns restart. The
supervisor's `executable` option launches the package directly; `dotnet` remains
supported for source builds, but the options are mutually exclusive. Relative
paths resolve against the config file. The private environment file is parsed as
KEY=VALUE data, never executed as shell code. Use absolute CA paths.

- Linux: the systemd user template owns bounded restart (three starts in five
  minutes). Install as the dedicated user only after reviewing paths. Do not
  create a second cron/supervisor owner. Same-user controls need the same /tmp
  namespace; PrivateTmp is deliberately disabled. Journal retention is managed
  by the OS; application logs rotate separately.
- macOS: launchd user template starts at login and does not auto-restart. If
  desired, the Python supervisor alone can perform up to three restarts. Before
  unloading use `production-host stop`, wait for exit, then unload the agent.
  Do not add KeepAlive on top of supervisor retries. Unsigned local candidates
  are not notarized distribution builds; verify provenance and use your normal
  organizational trust procedure. Never disable Gatekeeper globally.
- Windows: import the task template under the dedicated user, replace paths,
  select the intended logon/security options, and leave restart disabled. The
  task refuses overlapping instances. Use `production-host stop` and wait for
  task exit; Task Scheduler End is not a graceful stop protocol. Do not rely on
  CTRL_BREAK when a scheduled task has no console. For unattended boot startup,
  configure that user's logon policy explicitly and test it on the target host;
  the shipped template is logon-triggered, not an installed Windows service.

Service-manager import/boot behavior must be verified on the deployment host;
CI tests the extracted application lifecycle, not machine reboot or OS registration.
No template contains passwords. No service is installed automatically.

## Stop, upgrade, recover

Use the same account and exact canonical database path for controls:
`production-host status <database>` and `production-host stop <database>`.
A stop reply accepts shutdown; wait for process exit before direct state access.
Allow 120 seconds for bounded network activity. If forced termination is needed,
preserve the database/WAL; inspect Uncertain work after restart, never replay it.

Disable the startup owner, stop, and preserve the newest original state plus a
verified evidence snapshot and all archive receipts. Record the old package
manifest. Extract the new package beside the old one, run its offline smoke,
then update only the executable path and start with the same original database.
Opening older state migrates it to schema 10. The old binary may no longer open
it; there is no in-place downgrade. Do not substitute an older backup to resume
original tags. Normal restart uses the latest original database; loss of that
state uses the guarded fresh-tag recovery runbook in state-backup-recovery.md.

Observe last-round UTC, failed/uncertain sessions, queue growth and current-value
changes. Published counts are not acceptance receipts. Reserve at least the
reference workload's 10 GiB planning allowance; increase it for retained
archives/backups and larger models. An intentional maintenance stop is outside
healthy live-latency measurements. A finite horizon still needs explicit extension.

Native template references: [Task Scheduler XML and principal settings](https://learn.microsoft.com/en-us/windows/win32/taskschd/logon-trigger-example--xml-). The template uses a dedicated non-elevated account selected by the operator; it does not adopt the administrator account in the documentation example.

## Docker (the supported deployment)

The owner runs the simulator only in Docker, never natively on macOS.

Smoke-tested November 21, 2026, on the Mac's Docker, on the Timebase stack network,
against Historian/Pulse 2.0.0:

- **1.0.1 `linux-arm64`, native:** passed. Catch-up 21 s, status p95 416 ms,
  live lag p95 1.27 s. The SIGINT stop and restart were verified.
- **1.0.0 `linux-x64` under x64 emulation:** passed. Status p95 966 ms.

Admission, backfill to live, extension, restart, pause and resume, and completion
all passed. The three-hour live acceptance ran on the rc.2 macOS package; the
owner will run a long Docker soak themselves. No image is shipped. Build one
locally from the package matching the host CPU:

```dockerfile
FROM debian:trixie-slim
ARG PACKAGE=industrial-data-sim-1.0.1-linux-arm64
RUN apt-get update && apt-get install -y --no-install-recommends libicu76 \
    && rm -rf /var/lib/apt/lists/* && useradd --create-home --uid 10001 datasim
COPY ${PACKAGE}/ /opt/datasim/
USER datasim
STOPSIGNAL SIGINT
ENTRYPOINT ["/opt/datasim/app/IndustrialDataSim.Cli"]
```

On an x64 host, build with `--build-arg PACKAGE=industrial-data-sim-1.0.1-linux-x64`.

Rules for running it:

- **ICU is required.** Slim images do not include it, and without it the CLI
  aborts at startup with "Couldn't find a valid ICU package". The library name
  follows the Debian release (`libicu76` on trixie, `libicu72` on bookworm).
- **Stop with SIGINT, never SIGTERM.** The host treats Ctrl+C (SIGINT) as a
  graceful stop, finishing the in-flight publish and exiting with code 130. It
  does not handle SIGTERM, Docker's default. A forced stop can leave a Sending
  batch Uncertain, and Uncertain batches are never replayed. Keep `STOPSIGNAL
  SIGINT` (or `--stop-signal SIGINT`), and set `--stop-timeout 120` so Docker does
  not kill the host mid-round. Alternatively, run
  `docker exec <container> /opt/datasim/app/IndustrialDataSim.Cli production-host stop <database>`
  first. This was verified with `docker stop` while backfill was publishing:
  exit 130, the session stayed Ready, and the restart continued forward.
- **Connect to Timebase by service name.** Attach the container to the stack's
  network (here `timebase-2-0-beta_default`) and use
  `TIMEBASE_BASE_URL=https://historian:4512` and
  `TIMEBASE_PULSE_URL=https://pulse:4542`. The stack certificates include those
  names, so TLS verifies against the mounted Pulse root CA
  (`TIMEBASE_CA_BUNDLE`, mounted read-only). Do not disable verification.
- **Credentials come from an env file** (`--env-file`, mode 0600, outside any
  repository). Never bake the client secret into the image.
- **State lives in a named volume on local disk.** Mount it at a fixed path
  (for example `/data`) and always use the same absolute database path, both
  for the host and for `docker exec` controls; the control pipe name derives
  from that exact path. One container owns one database: never run two hosts
  against the same volume, and do not use network or synced storage.
- **Restart owner:** use Docker's restart policy (`--restart unless-stopped`) as
  the single restart owner. Do not also run the Python supervisor inside the
  container.
- **Run controls inside the same container.** Use `docker exec` for
  `production-live status|pause|resume|extend` and similar commands, because the
  control pipe is same-user and same-container only.
- **Admit sessions with a one-off container** that mounts the same volume
  (`production start /data/state.db /models/<model>.json`), and do it before
  starting the host, or while it runs via `docker exec`.
- **Match the package to the host CPU.** On ARM64, the x64 package works
  only under emulation, and its status latency is roughly double.

Example:

```bash
docker volume create datasim-state
docker run -d --name datasim --restart unless-stopped --stop-signal SIGINT --stop-timeout 120 \
  --network timebase-2-0-beta_default --env-file /path/to/private/datasim.env \
  -v datasim-state:/data -v /path/to/pulse-root-ca.pem:/certs/pulse-root-ca.pem:ro \
  datasim:1.0.1 production-host run /data/state.db
```
