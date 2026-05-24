# Public API & External Connectors

The public API is hosted in `Muster.Web` under `/api/v1` (minimal API; the Wolverine.HTTP
pipeline is also mapped for future CQRS-style endpoints). It exists so external systems —
most importantly **"Coin" loot/economy connectors** — can read participation data and
mint/spend spendable currencies.

## Authentication

- **API-key** auth via the `ApiClient` entity, scoped per guild.
- Keys (`msk_…`) are generated in the web UI (Admin → API clients), shown **once**, and
  stored only as a **SHA-256 hash**.
- Each client has a set of **scopes**. A request must present `X-Api-Key`, target its own
  guild, and hold the required scope, or it gets 401/403.

## Endpoints (v1)

| Method | Route | Scope | Purpose |
| --- | --- | --- | --- |
| GET | `/api/v1/guilds/{guildId}/leaderboard?top=` | `read:leaderboard` | season leaderboard |
| GET | `/api/v1/guilds/{guildId}/members/{userId}/wallets` | `read:wallets` | balances per currency |
| GET | `/api/v1/guilds/{guildId}/ledger?skip=&take=` | `read:ledger` | paged ledger entries |
| POST | `/api/v1/guilds/{guildId}/currencies/{code}/mint` | `write:currency` | credit a currency |
| POST | `/api/v1/guilds/{guildId}/currencies/{code}/spend` | `write:currency` | debit a currency |

Write bodies are `{ "userId": 123, "amount": 50, "reason": "…" }`. They append to the ledger
(`SourceType = Connector`) through the same `AwardService` as in-app awards, so balances and
the outbox stay consistent. `spend` is overdraft-checked for currencies Muster is authoritative
for (returns `409 insufficient_funds`); `External`-mode currencies skip the check.

## Currency modes (balance authority)

Each `Currency` has a `Mode` describing where the balance authority lives:

| Mode | Authority | API behavior |
| --- | --- | --- |
| `Internal` (default) | Muster owns the balance | mint/spend mutate the ledger; spend is overdraft-checked |
| `External` | the external system owns the balance | Muster keeps a shadow ledger; spend skips the overdraft check |
| `Hybrid` | split: Muster mints (earning), external owns spend | Muster is authoritative for credits; spends reconcile via events |

In all modes the ledger remains the **audit trail**, and `LedgerEntryRecorded` events flow out
through the durable outbox for connectors to consume.

## Outbound integration (the "Coin" hook)

When a ledger entry commits, a `LedgerEntryRecorded` message is published through Wolverine's
**durable outbox** (see [messaging.md](./messaging.md)). Outbound connectors subscribe to these
events to drive external loot systems — reliably, with retries, and without coupling the bot or
web to any specific external service. This is the mechanism behind `External` and `Hybrid`
currency modes (Muster emits; the external system reconciles). Concrete webhook/HTTP connectors
are post-v1.

## Versioning

The route prefix is versioned (`/api/v1`). Breaking changes introduce `/api/v2`; contracts
live in `Muster.Contracts` so clients and connectors share definitions.
