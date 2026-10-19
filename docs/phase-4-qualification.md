# Phase 4 — packaging, installation and compatibility

October 19, 2026. Candidate version **0.4.0-phase4.1** is a packaging milestone,
not a release approval or the seven-day RC result. No existing state was migrated,
no service installed, and no Historian samples written during this phase.

## Delivered

- Self-contained RID-specific ZIPs for Linux x64, macOS ARM64 and Windows x64.
  Build source revision/dirty marker, SQLite/archive versions, dependency inventory,
  bundled runtime notices, package license metadata and per-file/archive SHA-256
  receipts travel with each package. The official Apache 2.0 license in third-party
  applies to SQLitePCLRaw, not to this repository as a whole.
- Fresh-extraction checks run from temporary directories with no TIMEBASE
  credentials and an invalid installed-runtime path. They verify the bundled
  runtime declaration, native SQLite, exclusive database ownership, two graceful
  start/stop cycles, restart and synthetic completion. Hash/unsafe-entry checks
  run before execution. Release CI refuses a dirty source manifest.
- Existing supervisor supports either a packaged executable or source-build
  dotnet, never both; state is not re-admitted. Platform templates have a single
  restart owner and are checked with native parsers without registration.
- Installation, private-directory permissions, startup/stop, side-by-side upgrade,
  recovery, maintenance and platform constraints are documented in installation.md.
- Current direct/transitive NuGet audit is required; audit warnings fail publish,
  and a separate checker rejects advisory/feed findings. Artifact content comes
  from an explicit source allowlist. No local deployment folders are collected.
- Local 2.0-gc1 Historian/Pulse image digests recorded. A TLS-verified read-only
  probe authenticated and verified Test Dataset response shape; purge age is
  seven days. This does not replace packaged adapter publish/arrival acceptance.

## Evidence

Local macOS 26.2 ARM64: self-contained .NET 10.0.12 candidate passed extraction,
ownership and restart checks. Python suite: 36 tests passed. Current NuGet audit
reported no vulnerabilities or feed problems. Native launchd plist validation
passed without registration. Existing .NET runtime behavior is unchanged;
the complete .NET suite and three-platform package checks run in CI.

CI implementation commit/run: pending.

Reproduce from a clean checkout with .NET 10 SDK and Python 3.12:

```text
python scripts/package_release.py --rid osx-arm64 --output .tools/candidate
python scripts/verify_package.py .tools/candidate/industrial-data-sim-0.4.0-phase4.1-osx-arm64.zip --require-clean
python scripts/verify_deployment_templates.py .tools/candidate/industrial-data-sim-0.4.0-phase4.1-osx-arm64
dotnet list src/IndustrialDataSim.Cli package --vulnerable --include-transitive --format json --output-version 1
```

Choose the matching RID. Use a new output directory for each build. CI retains
ZIP/checksum/audit artifacts for 14 days; exact release artifacts require the
separate immutable v1 release workflow/approval. Candidate manifests distinguish
source commit from product version. Builds are not claimed byte-for-byte reproducible.

## Review findings addressed

- `dotnet publish` omits license files: package assembly now copies runtime notices
  and NuGet license metadata explicitly, plus the Apache license text.
- The local Python lacks hashlib.file_digest: hashing now streams bounded chunks
  using the portable hashlib interface. No full binary is loaded for hashing.
- A temporary-directory assertion initially overlooked macOS /var symlink
  canonicalization; the test now compares canonical paths, as the launcher does.
- Startup templates must not confuse a stop acknowledgment with process exit.
  The runbook requires waiting; systemd uses SIGINT and a 120-second stop deadline,
  and Windows hard termination is disabled in the template.
- Dependency listing alone can exit successfully with findings: CI additionally
  parses audit JSON and refuses vulnerabilities or feed problems.

## Remaining release gates

V14 is scoped to the declared application packages and synthetic lifecycle.
Native template parsing is not an installed-service/boot test. The selected
Phase 5 deployment must exercise its actual startup owner, account permissions,
stop deadlines and machine restart. No bare-OS/native-library or broader CPU
support claim is made beyond compatibility.md.

V15 retains two explicit owner/operational decisions: no repository license was
selected, and private-CA revocation checking is not implemented. Trust/credential
rotation, signing/notarization requirements and distribution licensing must be
reviewed before external release. No credentials are embedded in candidates.

V16's exact local stack identity/read-only baseline is captured; real adapter
publish/arrival and long-running behavior belong to Phase 5. The 72-hour backfill,
seven-day live soak, user pattern review and final exact-candidate audit remain
mandatory. Retention can remove early backfill during the soak, so record its
arrival/pattern evidence before it ages out. Missing samples never authorize replay.
