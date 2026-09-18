# Industrial Data Simulator

A planned industrial data simulation tool that writes Timestamp Value Quality
(TVQ) points to Timebase Historian Datasets.

## Status

Initial API exploration is complete. Authentication, certificate verification,
automatic tag creation, and batched writes were exercised against a development
installation. This repository contains documentation and configuration templates;
it does not yet contain a runnable simulator.

See [API findings](docs/timebase-api-findings.md) for payloads, observed behavior,
and unresolved questions.

See the [Phase 1 implementation plan](docs/phase-1-plan.md) for the proposed
session engine, conditional models, durable buffering, and acceptance criteria.

Dataset naming policy: use letters, digits, hyphens (`-`), underscores (`_`),
and spaces only; avoid other special characters.

## Local configuration

Copy `.env.example` to `.env` and populate it locally. No configuration loader
has been implemented yet. Keep `.env` owner-readable only. For deployments,
supply credentials through a secret manager or environment variables.

| Variable | Purpose |
| --- | --- |
| `TIMEBASE_BASE_URL` | HTTPS origin of the Historian service |
| `TIMEBASE_PULSE_URL` | HTTPS origin of the Pulse service |
| `TIMEBASE_CLIENT_ID` | OAuth client identifier configured in Pulse |
| `TIMEBASE_CLIENT_SECRET` | OAuth client secret; never commit it |
| `TIMEBASE_AUDIENCE` | Intended API audience configured in Pulse |
| `TIMEBASE_DATASET` | Destination Dataset name |
| `TIMEBASE_CA_BUNDLE` | Path to the deployment's trusted PEM CA bundle |

Obtain the public root CA from the service administrator. A server certificate
is distinct from its issuing CA. Chain and hostname verification both succeeded
with the supplied root CA during development. Keep TLS verification enabled.
Certificate files under `certs/` are local-only and excluded from Git. No
system-wide trust installation is required for a client-specific CA bundle.

## Ordering requirement

The simulator must write strictly increasing timestamps **per tag**, within and
across batches. No historical inserts, backward writes, or equal-timestamp
replacements are permitted. This is a simulator requirement: the tested
Historian accepted older points, so HTTP success cannot enforce this rule.

Read the latest timestamp before resuming an existing tag. Submit batches
sequentially per tag and coordinate writers to prevent races. After an ambiguous
write response or timeout, reconcile stored points before retrying; do not
blindly replay a batch. These are requirements, not implemented safeguards.

## Repository hygiene

Commit only placeholders and synthetic examples. Keep credentials, tokens,
private keys, deployment certificates, local configuration, screenshots,
diagnostic dumps, and logs outside version control. The example configuration
contains no working credentials or deployment-specific identifiers. Review
staged content before each commit; ignore rules alone are not a secret scanner.
