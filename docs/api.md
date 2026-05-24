# Public API & External Connectors

The public API is authored with **Wolverine.HTTP** and hosted in `Muster.Web` under
`/api/v1`. It exists so external systems — most importantly **"Coin" loot/economy
connectors** — can read participation data and mint/spend spendable currencies.

> **Status:** API surface is scaffolded in M0 (Wolverine.HTTP wired). Concrete endpoints
> land in M5; outbound connectors are post-v1.

## Authentication

- **API-key** auth via the `ApiClient` entity, scoped per guild.
- Keys are generated in the web UI, shown **once**, and stored **hashed**.
- Each client has a set of **scopes** (e.g. `read:ledger`, `read:leaderboard`,
  `write:currency`).

## Endpoints (v1 target)

| Method | Route | Scope | Purpose |
| --- | --- | --- | --- |
| GET | `/api/v1/guilds/{guildId}/leaderboard` | `read:leaderboard` | season leaderboard |
| GET | `/api/v1/guilds/{guildId}/members/{userId}/wallets` | `read:ledger` | balances per currency |
| GET | `/api/v1/guilds/{guildId}/ledger` | `read:ledger` | paged ledger entries |
| POST | `/api/v1/guilds/{guildId}/currencies/{code}/mint` | `write:currency` | credit a spendable currency |
| POST | `/api/v1/guilds/{guildId}/currencies/{code}/spend` | `write:currency` | debit a spendable currency |

Write endpoints are **guarded** and append to the ledger (`SourceType = Connector`), going
through the same handlers as in-app awards so balances and the outbox stay consistent.

## Outbound integration (the "Coin" hook)

When a ledger entry commits, a `LedgerEntryRecorded` message is published through Wolverine's
**durable outbox** (see [messaging.md](./messaging.md)). Post-v1, outbound connectors
subscribe to these events to drive external loot systems — reliably, with retries, and
without coupling the bot or web to any specific external service.

## Versioning

The route prefix is versioned (`/api/v1`). Breaking changes introduce `/api/v2`; contracts
live in `Muster.Contracts` so clients and connectors share definitions.
