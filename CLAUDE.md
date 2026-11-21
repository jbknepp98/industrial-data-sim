# Start here — Claude Code handoff

Read `AGENTS.md` first. Its human-readable code, actionable errors, bounded safe
logging, test and documentation rules apply to this project. Then read
`docs/claude-code-handoff.md`, followed by the release specification and acceptance
matrix linked there.

v1.0.0 is released (tag `v1.0.0`, November 21, 2026; see
`docs/release-notes.md`). It is deployed in Docker for internal use. Any
change needs a new version number and the affected acceptance repeated.
Never open historical demo databases with the current runtime merely to inspect
them: opening can migrate state. Never automatically replay Uncertain work or
resume older backup Pending work on original tags. Do not print or commit secrets.

A transferred archive is a development handoff, not authorization to start hosts,
install services, change the Timebase stack, or publish historical values. Confirm
the current deployment and intended run with the user before new live acceptance.
Use the latest original database for normal restart. Disaster restore permanently
quarantines prior sessions and requires fresh tag names.

The handoff includes a suggested first-session prompt, architecture map, verified
commits, reproduction commands, known limits and a concrete next-work checklist.
