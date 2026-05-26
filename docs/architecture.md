# Architecture

## Goals

- **Multi-tenant**: a single deployment serves many Discord guilds; all data is guild-scoped.
- **Small footprint v1, scale later**: ship as a couple of containers, with a clear path to
  horizontal scale and Discord sharding without re-architecting.
- **Reliable rewards**: participation is captured exactly once and recorded in an append-only
  ledger that can later drive external economies.

## Topology

Two long-lived deployables plus a run-once migration job, all sharing one Azure SQL database.

```
                  ┌──────────────────────────────────────────────┐
                  │              Azure Container Apps               │
  Discord  ◄─WSS─►│  muster-bot   (NetCord gateway, singleton)      │
                  │     gateway events → Wolverine commands         │
                  │                                                 │
  Browser  ◄HTTP─►│  muster-web   (Blazor SSR + Wolverine.HTTP API) │
                  │     scales 1..N                                 │
                  │                                                 │
                  │  muster-migrations  (run-once Job)              │
                  └───────────────────────┬─────────────────────────┘
                                          ▼
        Azure SQL (EF Core)  •  Key Vault  •  ACR  •  Log Analytics / App Insights
```

### muster-bot (stateful)

Holds the persistent Discord gateway WebSocket. A bot token maps to a fixed number of
**shards**, and each shard is a single stateful connection — so the bot **must run as one
replica per shard set**. In v1 that is a single shard on a single replica
(`min = max = 1`). It does minimal work itself: it translates gateway events (voice state,
reactions, scheduled events, messages) into **Wolverine commands** and lets handlers apply
business rules.

### muster-web (stateless)

Blazor static SSR site plus the Wolverine.HTTP API. Holds no session affinity beyond the
auth cookie, so it scales horizontally (`1..N`). Admin actions and API calls become
Wolverine commands handled against the shared database.

### muster-migrations (run-once)

Applies EF Core migrations (including Wolverine's durable-messaging tables) on deploy, then
exits. Prevents the bot/web from blindly auto-migrating in production. Runs as a Container
Apps job in Azure; locally it also seeds development data.

## How the services coordinate

In v1 there is **no message broker**. The two services integrate through:

1. The **shared Azure SQL database** (the system of record).
2. **Wolverine's durable outbox/inbox** (EF Core + SQL Server) for transactional,
   retry-safe event handling within each process.

Message **contracts** (`Muster.Contracts`) are written to be broker-agnostic. When load
justifies it, enabling the **Azure Service Bus** transport turns bot→web publishes into
real cross-container delivery with no handler changes. See [messaging.md](./messaging.md).

## Scaling path

| Concern | v1 | Later |
| --- | --- | --- |
| Web throughput | 1 replica | scale ACA replicas 1..N (stateless) |
| Bot guild count | 1 shard, 1 replica | Discord sharding; shards spread across replicas |
| Bot ↔ web messaging | in-process + DB | Azure Service Bus transport (config toggle) |
| External economies | outbox + guarded API | dedicated "Coin" connectors consuming the outbox |

Discord recommends roughly **one shard per 2,500 guilds**. Sharding is designed for but not
implemented in v1; the bot worker is structured so shard assignment can later be derived
from the Container Apps replica index.

## Cross-cutting concerns

- **Idempotency**: unique indexes on natural keys (message id, `(muster, user)`,
  `(sourceType, sourceId)` on the ledger) make gateway redelivery / RESUME safe.
- **Observability**: `Muster.ServiceDefaults` wires OpenTelemetry traces/metrics/logs and
  health endpoints into every service; exported to Application Insights in Azure.
- **Secrets**: Discord token and OAuth credentials are Aspire parameters → user-secrets
  locally, Key Vault references in Azure.
- **Time**: each guild has an IANA time zone used for scheduling and reporting.
