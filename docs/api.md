# Public API & External Connectors

The public API is hosted in `Muster.Web` under `/api/v1`, authored as **Wolverine.HTTP**
endpoints (discovered by assembly scanning). It exists so external systems — most importantly
**"Coin" loot/economy connectors** — can read participation data and mint/spend spendable
currencies. Currency writes are slated to become Wolverine **commands** (`MintCurrency` /
`SpendCurrency`) so the whole surface is CQRS end-to-end.

## Connector transports (planned)

External/Hybrid currencies will reach their backing system through a pluggable transport,
driven by the durable outbox: **Webhook**, **HTTP API**, or **Discord message command**
(for economy bots that only accept prefix/message commands — note Discord does not allow
bots to invoke other bots' *slash* commands). Each currency names its transport + settings.

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

### Quests (`ApiQuestEndpoints`)

The quest API mirrors the bot/web: every write **invokes the same CQRS command** via `IMessageBus`,
so the command handler is the single authorization funnel.

| Method | Route | Scope | Purpose |
| --- | --- | --- | --- |
| GET | `/api/v1/guilds/{guildId}/quests?tab=&type=&search=&sort=&desc=&page=&size=` | `read:quests` | board (filter/search/sort/page) |
| GET | `/api/v1/guilds/{guildId}/quests/{questId}` | `read:quests` | detail + participants, reviewers, dispute |
| POST | `…/quests` | `write:quests` | post a quest |
| POST | `…/quests/{id}/claim` | `write:quests` | claim *as* a member |
| POST | `…/quests/{id}/submit` | `write:quests` | submit *as* a member |
| POST | `…/quests/{id}/approve` | `write:quests` | approve a submission (mint) |
| POST | `…/quests/{id}/reject` | `write:quests` | reject a submission |
| POST | `…/quests/{id}/request-revision` | `write:quests` | send back for revision |
| POST | `…/quests/{id}/reopen` | `write:quests` | undo a reject (`{ "memberId": 123 }`) |
| POST | `…/quests/{id}/confirm` | `write:quests` | owner accepts + pays a player quest |
| POST | `…/quests/{id}/dispute` | `write:quests` | owner/taker raises a dispute |
| POST | `…/quests/{id}/cancel` | `write:quests` | cancel |
| POST | `…/quests/{id}/arbitrate` | `write:quests` | resolve a dispute (`{ "pay": true }`) |
| POST | `…/quests/{id}/intake/accept` | `write:quests` | accept + tier a pending quest |
| POST | `…/quests/{id}/intake/reject` | `write:quests` | reject at intake (refund) |
| POST | `…/quests/{id}/finalize` | `write:quests` | final sign-off (`{ "pay": true }`) |
| POST | `…/quests/{id}/edit` | `write:quests` | patch before work (`{ "name", "reward", "tier", … }`) |

**Two-layer auth.** A request passes only if **both** hold:
1. **Token scope** — the key holds `read:quests` / `write:quests` (which endpoints it may call).
2. **Bound actor** — the key is tied to a Discord user (`ActsAsUserId`). Every action runs *as that user* with
   *their* guild roles, and the command funnel authorizes it exactly as for the bot/web. So a key can do at most
   the **intersection** of its scopes and its actor's permissions — bind a GuildMaster for manager actions.
   **A key may only be bound to the creating admin's own account or a bot/app member of the guild** — never an
   arbitrary member (prevents impersonating a non-consenting user); this is enforced server-side at creation.
   Bots are synced (with an `IsBot` flag) so they can serve as service actors, but are hidden from human-facing
   pickers (leaderboard, award). Removing the actor's role — or kicking them — instantly neutralizes the key.

The actor is **never supplied in the request** — a key can't act on behalf of anyone but its bound user. (`memberId`
on review bodies is the *subject* — whose submission — not the actor.) A key with no actor gets `403 key_not_bound`;
a transition the actor isn't allowed to make returns `409 quest_action_failed` (reason `Forbidden`).

Bodies: `post` `{ "origin": "Guild|Player", "name": "…", "currency": "COIN", "reward": 50, "tier": "B" }`;
`submit` `{ "note": "…" }`; `approve`/`reject` `{ "memberId": 123, "note": "…" }`; `request-revision`
`{ "memberId": 123, "note": "…" }`; `arbitrate`/`finalize` `{ "pay": true }`; `intake/accept`
`{ "tier": "B", "requireFinalApproval": false }`; `claim` / `cancel` / `intake/reject` take no body.
A failed transition returns `409` with `{ "error": "quest_action_failed", "reason": "<QuestResult>" }`.

The list (`GET /quests`) mirrors the web board: `tab` (`active`|`actionneeded`|`history`, default `active`; `actionneeded` =
the manager review queue, empty for non-managers), `type`/scope (`guild`|`player`|`mine`, default all; `mine` = bounties you posted + any quest you claimed, incl. guild duties),
`search`, `sort` (`reward`|`closes`|`created`|`name`|`type`|`status`|`opens`), `desc`, `page`, `size` (10/25/50/100). It returns
`{ items, total, page, totalPages, codes, names, avatars }`. `mine`/`history` and the manager view (submissions-to-review, pending-intake
rows) resolve against the key's **bound actor** + `IsQuestManagerAsync` — an unbound key sees the public active board as a non-manager.

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
