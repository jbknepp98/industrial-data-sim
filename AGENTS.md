# Development process

Work in small, reviewable increments. Check each change with appropriate tests
and update the relevant documentation before moving to the next increment.

## Human-readable code

All code must remain understandable to a human maintainer, even though agents
author simulation definitions. Use descriptive names, explicit control flow,
and focused methods. Avoid clever shortcuts and unnecessary abstractions.
Document the reasons for non-obvious decisions, timing and ordering invariants,
boundary conditions, and recovery behavior next to the relevant code. Comments
must explain intent and stay consistent with actual behavior; do not merely
repeat the implementation.

## Useful errors

Treat every returned error as part of the public interface. Review all failure
paths touched by a change, including validation, CLI usage, file access,
generation, and future persistence and Historian delivery failures.

Each error must explain what failed, identify the affected field or operation,
and provide a concrete correction or troubleshooting step whenever possible.
State relevant limits, expected types, units, or allowed values. Distinguish a
confirmed cause from a possible cause; do not claim a cause that was not verified.
Use stable machine-readable error codes and structured locations alongside
plain-language messages. Preserve documented failure exit codes.

Never expose credentials, tokens, private keys, raw input documents, or raw
exception details in returned errors. Prefer schema field paths and safe
operation context. Redaction must not reduce errors to an unexplained failure:
for example, a file-read error can suggest checking existence and permissions
without echoing its path or contents.

Keep diagnostic collection and serialized responses bounded. If diagnostics are
omitted, explicitly explain the limit and tell the caller to correct the reported
issues and rerun validation. Never emit partial JSON or imply success because
errors were truncated.

For each changed failure path, verify the error code, location, explanation,
corrective guidance where applicable, and failure status. Test important edge
cases and confirm diagnostics do not disclose sensitive input. Review existing
errors against these rules during repairs; record remaining gaps honestly.

## Operational logging

Treat log messages as a human-facing troubleshooting interface. Use stable event
codes, a plain-language explanation, and a concrete next action when needed.
Include only reviewed context such as session/batch IDs and numeric positions;
never log configuration, values, credentials, paths, or raw exception details.
Keep volume bounded, report changed conditions rather than repeated polling,
and test that logger failures cannot change durable state or delivery behavior.
Logs are observations, never authority for recovery or automatic replay.

## Completion review

Before reporting an increment complete, check human readability, explanatory
comments, actionable errors, appropriate test results, and agreement between
code and active documentation. Do not claim unreviewed code or error paths meet
these standards. Keep secrets and local deployment artifacts out of commits.
