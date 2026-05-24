# Operations

## Observability

`Muster.ServiceDefaults` wires **OpenTelemetry** (traces, metrics, logs) and health checks
into every service. Locally these surface in the Aspire dashboard; in Azure they export to
**Application Insights** via the OTLP endpoint.

- Health endpoints: `/health` (readiness) and `/alive` (liveness), exposed in development.
- Add alerts on: bot gateway disconnects, migration-job failures, SQL DTU/error spikes,
  API 5xx rate, and outbox backlog growth.

## Bot resilience

- The gateway worker should shut down gracefully so Discord can **RESUME** the session
  cleanly on the next start.
- Idempotency indexes (`SourceMessageId`, `(SourceType, SourceId)`) make event redelivery
  after a reconnect safe — no double-counting.

## Data retention & privacy

Muster stores Discord user data (ids, names, participation). Plan for:

- **Retention**: roll raw `ActivityRecord` into `DailyActivityRollup` and prune raw rows on
  a schedule; keep the ledger (it's the audit trail).
- **Deletion**: support per-member data deletion on request (and on guild removal, mark the
  guild inactive and schedule purge).
- **Discord ToS**: a verified bot needs published **Privacy Policy** and **Terms of
  Service** pages — link them from the web app (M7).

## Secrets

- Stored in **Key Vault**; referenced by Container Apps via managed identity.
- SQL access is **passwordless** (Entra) — there is no DB credential to rotate.
- Rotate the Discord bot token and OAuth secret on a schedule and on suspected compromise;
  update Key Vault and restart revisions.

## Backups & recovery

- Rely on Azure SQL automated backups / point-in-time restore.
- The ledger is append-only, so balances can be **rebuilt** by replaying entries into
  `Wallet` if a projection is ever corrupted.

## Runbooks (stubs to expand in M7)

- **Bot won't connect**: check token in Key Vault, gateway intents, Discord status.
- **Migration job failed**: inspect the job logs; fix forward with a new migration (do not
  hand-edit the database); re-run the job before rolling app revisions.
- **Scores look wrong**: query the ledger for the member/source; rebuild the affected
  wallet from ledger entries.
- **Outbox backlog**: check handler exceptions; Wolverine retries — investigate poison
  messages in the dead-letter store.
