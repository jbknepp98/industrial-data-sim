# Phase 4 security and dependency review

The candidate is public source plus explicitly allowed compiled runtime files,
public docs, one synthetic example and startup templates. Private environment,
certificates, state, logs and ignored tooling are excluded. Each candidate has
per-file hashes, an archive checksum, source commit/dirty marker, dependency
inventory and bundled third-party runtime notices and NuGet license metadata. A dirty build is local evidence
only. Hashes detect corruption; independently authenticate the source/run before
trusting them. Candidate ZIPs are not code-signed/notarized release artifacts.

The build enables NuGet audit for direct and transitive dependencies; warnings
are errors. Network/feed failures must fail the build. A separate CI JSON audit
is retained beside artifacts. No advisory result is a permanent safety guarantee;
repeat on the exact release candidate. Dependencies are resolved from declared
versions; the shipped deps inventory records the actual runtime selections.

Reviewed boundaries: HTTPS origins only; redirect forwarding disabled; private
CA validation retains hostname, validity and server-auth checks; write requests
are not automatically retried. Custom CA validation currently does not check
revocation. Use short-lived certificates/controlled trust rotation; revocation
support is not claimed. This is an operational constraint, not a disabled hostname
check. Existing TLS, authorization, transport and logging tests remain required.

Only the same OS user can access control pipes, but that user and administrators
can inspect process environment/state. Operators must enforce private-directory
permissions before startup. The app does not promise to repair insecure ACLs.
Startup JSON contains paths only. Templates never install themselves; a single
restart owner prevents restart storms. State ownership is local, not distributed.
Separate databases/external writers must have disjoint tag assignments.

Runtime diagnostics redact raw configuration, payloads, paths and transport
exceptions. Build tools and private build logs are developer diagnostics and may
contain paths; do not publish those logs as ordinary operational reports. Package
verification uses synthetic state and strips TIMEBASE credential variables.

No repository license grant has been selected by the owner. Public visibility is
not a license. Third-party runtime notices remain bundled; choosing a project
license and reviewing distribution obligations are release-owner decisions before
external distribution. This document does not invent that permission.

Review results and remaining qualification are recorded in phase-4-qualification.md.
