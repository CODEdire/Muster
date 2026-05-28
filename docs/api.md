# Public API & External Connectors

The public API is hosted in `Muster.Web` under `/api/v1`, authored as **Wolverine.HTTP**
endpoints (discovered by assembly scanning). It exists so external systems — most importantly
**"Coin" loot/economy connectors** — can read participation data and mint/spend spendable
currencies. Currency writes are Wolverine **commands** (`MintCurrency` / `SpendCurrency`), so the
whole surface is CQRS end-to-end.

## Connector transports

External/Hybrid currencies reach their backing system through a pluggable **outbound transport**,
driven by the durable outbox (see [Outbound integration](#outbound-integration-the-coin-hook)).
Each currency names one transport + settings (Admin → Currencies → *External connector*):

- **Webhook** *(implemented)* — HTTP `POST` of the movement JSON; when a secret is set the body is
  signed `X-Muster-Signature: sha256=<hmac>` (HMAC-SHA256), with `X-Muster-Delivery: <ledgerEntryId>`.
- **HTTP API** *(implemented)* — HTTP `POST` with the secret in an auth header (default
  `Authorization: Bearer …`; a custom header name sends the secret verbatim, e.g. `x-api-key`).
- **Discord message command** *(wishlist)* — for economy bots that only accept prefix/message
  commands (Discord doesn't allow bots to invoke other bots' *slash* commands).

**Body template (target any API).** By default a connector sends Muster's native payload (below). To match a
specific economy API's contract, set a **body template** on the currency's connector — tokens use a `$name` syntax
(no braces, so the template stays **valid JSON** and highlights in the editor). Numbers: `$userId` `$amount`
`$guildId` `$deliveryId` — write them **quoted** (`"$amount"`) and the quotes are stripped on send, so the template
parses as JSON yet emits a number. Strings: `$displayName` `$currencyCode` `$reason` `$sourceType` `$occurredAt`
render as JSON-escaped content inside the template's quotes. Example — the **Cruor** loot service's
`POST /currency/add-cruor` (`x-api-key` header):

```json
{ "member_id": "$userId", "display_name": "$displayName", "cruor_amount": "$amount" }
```

`$amount` is signed, so a debit sends a negative number (targets that accept negatives reconcile spends directly).

## Authentication

- **API-key** auth via the `ApiClient` entity, scoped per guild.
- Keys (`msk_…`) are generated in the web UI (Admin → API clients), shown **once**, and
  stored only as a **SHA-256 hash**.
- Each client has a set of **scopes**. A request must present `X-Api-Key`, target its own
  guild, and hold the required scope, or it gets 401/403.

## Endpoints (v1)

| Method | Route | Scope | Purpose |
| --- | --- | --- | --- |
| GET | `/api/v1/guilds/{guildId}/leaderboard?top=&currency=` | `read:leaderboard` | leaderboard — season POINTS by default, or top holders of `?currency=CODE` |
| GET | `/api/v1/guilds/{guildId}/members/{userId}/wallets` | `read:wallets` | balances per currency |
| GET | `/api/v1/guilds/{guildId}/ledger?skip=&take=` | `read:ledger` | paged guild-wide ledger entries |
| GET | `/api/v1/guilds/{guildId}/members/{userId}/ledger?currency=&skip=&take=` | `read:ledger` | one member's history (newest first, optional currency filter) |
| GET | `/api/v1/guilds/{guildId}/currencies` | `read:wallets` | list the guild's currencies |
| GET | `/api/v1/guilds/{guildId}/members/{userId}/currencies/{code}/balance` | `read:wallets` | one currency's balance |
| GET | `/api/v1/guilds/{guildId}/currencies/{code}/supply` | `read:ledger` | supply analytics (minted/removed/circulating/escrow + holders) |
| GET | `/api/v1/guilds/{guildId}/currencies/{code}/movements?skip=&take=` | `read:ledger` | guild-wide recent movements for one currency (newest first) |
| GET | `/api/v1/guilds/{guildId}/audit?action=&search=&page=&pageSize=` | `read:audit` | admin audit trail (who minted/adjusted/sent/configured), filterable + paged |
| POST | `/api/v1/guilds/{guildId}/currencies/{code}/mint` | `write:currency` | credit a currency (machine/inbound mirror) |
| POST | `/api/v1/guilds/{guildId}/currencies/{code}/spend` | `write:currency` | debit a currency (machine/inbound mirror) |
| POST | `/api/v1/guilds/{guildId}/currencies/{code}/transfer` | `write:currency` (actor-bound) | member-to-member move |
| POST | `/api/v1/guilds/{guildId}/currencies/{code}/adjust` | `write:currency` (actor-bound) | staff signed correction / mint |

`mint`/`spend` bodies are `{ "userId": 123, "amount": 50, "reason": "…", "externalId": "…" }`. They are the
**machine inbound** path — an external economy telling Muster to mirror a movement it already made. They append
to the ledger (`SourceType = Connector`, so they're **never echoed back out**) through the same `CurrencyService`
as in-app awards. `spend` is overdraft-checked for currencies Muster is authoritative for (returns
`409 insufficient_funds`); `External`-mode currencies skip the check.

`transfer` / `adjust` are the **user/staff CQRS** path (Wolverine `IGuildCommand` → authorized + audited). They
run *as the key's bound actor* (`ActsAsUserId`), so the key **must be actor-bound** (else `403 key_not_bound`):
- `transfer` body `{ "toUserId": 456, "amount": 50, "reason": "…", "fromUserId": 0 }` — omit/zero `fromUserId` to
  move from the actor's own wallet. The authorizer allows members to move only their own wallet; staff may move
  anyone's. Both legs of an `External`/`Hybrid` transfer push to the backing system before commit (`SourceType = Transfer`).
- `adjust` body `{ "userId": 123, "delta": -50, "reason": "…", "externalId": "…" }` — positive mints, negative
  deducts; **economy staff only** (officers + admins). `SourceType = Adjustment`.

All four return `{ "balance": <resulting> }` on success.

`externalId` is **optional but recommended** — it makes the write **idempotent**: a connector that
retries the same delivery won't double-apply (deduped via the ledger's `(SourceType, SourceId)` unique
key, where `SourceId = "connector:{externalId}"`). Connector-origin entries are also never echoed back
out (the outbound dispatcher skips `SourceType = Connector`), so inbound writes can't loop.

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

### Participation tracking (`ApiTrackingEndpoints`)

Parity with the bot's `/track` tree and the web Sessions/Multipliers pages — reads use `read:tracking`,
writes use `write:tracking`, all reusing the same domain services.

| Method | Route | Scope | Purpose |
| --- | --- | --- | --- |
| GET | `/api/v1/guilds/{guildId}/tracking/leaderboard?top=` | `read:tracking` | top members by voice time |
| GET | `/api/v1/guilds/{guildId}/tracking/sessions/active?page=&pageSize=` | `read:tracking` | active sessions |
| GET | `/api/v1/guilds/{guildId}/tracking/sessions?page=&pageSize=` | `read:tracking` | closed session history |
| GET | `/api/v1/guilds/{guildId}/tracking/sessions/{sessionId}` | `read:tracking` | session detail + roster (no event stream) |
| GET | `/api/v1/guilds/{guildId}/tracking/sessions/{sessionId}/events?page=&pageSize=` | `read:tracking` | presence-event audit log (join/resume/pause/leave) |
| GET | `/api/v1/guilds/{guildId}/members/{userId}/tracking` | `read:tracking` | member voice stats |
| GET | `/api/v1/guilds/{guildId}/tracking/channels` | `read:tracking` | monitored channels |
| GET | `/api/v1/guilds/{guildId}/tracking/multipliers` | `read:tracking` | reward multipliers |
| POST | `…/tracking/sessions` | `write:tracking` (actor-bound) | open a session `{ channelId, name, skipMuted?, skipDeafened?, skipAlone? }` |
| POST | `…/tracking/sessions/{sessionId}/stop` | `write:tracking` | close + award |
| POST | `…/tracking/sessions/{sessionId}/optout` | `write:tracking` (actor-bound) | opt a member out of this session `{ userId }` |
| PUT | `…/tracking/channels/voice` | `write:tracking` | monitor voice `{ channelId, pointsPerMinute, dailyCap, requireUnmuted, requireUndeafened, requireNotAlone }` |
| PUT | `…/tracking/channels/text` | `write:tracking` | monitor text `{ channelId, pointsPerMessage, messagesPerPoint, cooldownSeconds, dailyCap }` |
| DELETE | `…/tracking/channels/{channelId}` | `write:tracking` | stop monitoring |
| POST | `…/members/{userId}/tracking/privacy` | `write:tracking` | set privacy `{ choice }` |
| POST | `…/tracking/multipliers` | `write:tracking` | create `{ kind: oneoff\|recurring\|role, name, factor, scope, … }` |
| POST | `…/tracking/multipliers/{id}/enabled?enabled=` | `write:tracking` | enable/disable |
| DELETE | `…/tracking/multipliers/{id}` | `write:tracking` | remove |

Writes return `{ "message": … }` on success or `400 { "error": … }` on a validation failure (the same
`CommandResult` the bot/web surface). Opening a session via the API can't scan the live voice roster (no
gateway on the API host), so members already present are credited on the bot's next reconcile sweep.

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

In all modes the ledger remains the **audit trail**, and every staged movement publishes a
`CurrencyMovementRecorded` message through the durable outbox for connectors/observers to consume.

## Outbound integration (the "Coin" hook)

For an `External`/`Hybrid` currency whose connector is enabled, the external Credit/Debit is called
**synchronously inside `CurrencyService.StageAsync`** (external-before-finalize): the movement must succeed
remotely before the ledger leg commits, so the bot/web/API operation aborts if the backing system is down.
The call carries a stable idempotency key (the ledger source) so a resilience retry can't double-apply. A
`CurrencyMovementRecorded` message is also published per movement (see [messaging.md](./messaging.md)) as the
observability/fan-out seam. Drift is reconciled by the GetBalance sweep + dashboard sync.

The outbound JSON payload (camelCase):

```json
{ "guildId": 123, "currencyCode": "COIN", "userId": 456, "amount": 50,
  "reason": "Quest approved: …", "sourceType": "Quest",
  "occurredAt": "2026-05-26T12:00:00Z", "deliveryId": 9876 }
```

- `deliveryId` = the ledger entry id, also sent as `X-Muster-Delivery` — the **idempotency key** the
  receiver dedupes on (delivery is at-least-once).
- The dispatcher **skips `sourceType = Connector`** entries, so an inbound (external-origin) write is
  never echoed back out (no loop).
- A non-2xx response throws, so Wolverine retries the durable message.

A connector **body template** (see [Connector transports](#connector-transports)) reshapes this payload to a
target API's field names; `displayName` is the member's Discord global/username (from the synced user).

Webhook + HTTP-API transports are implemented; a Discord message-command transport is wishlist.

### Outbound movement webhooks (per-guild event fan-out)

Distinct from the per-currency connector above: a guild admin can register **webhook subscriptions** (Admin →
Webhooks) that receive **every** currency movement, not just one currency's external sync. Each enabled
subscription whose source filter matches gets an HMAC-SHA256-signed POST of the movement:

```json
{ "guildId": 123, "userId": 456, "currencyId": "…", "currencyCode": "COIN", "amount": 50,
  "source": "Transfer", "sourceId": "…", "reason": "…", "seasonId": null, "occurredAt": "2026-05-27T…Z" }
```

- Headers: `X-Muster-Signature: sha256=<hmac of the raw body>` (when a secret is set), `X-Muster-Delivery`
  (stable per-movement idempotency key), `X-Muster-Event: currency.movement`.
- **Source filter** narrows which movements fire (empty = all); checkpoints never publish, so pruning is excluded.
- Delivery is **best-effort with health tracking** — consecutive failures accrue and auto-disable the subscription
  past a threshold (admins re-enable, which clears the streak). Managed in the web UI (add / test / disable / delete);
  separate from API keys.

## Versioning

The route prefix is versioned (`/api/v1`). Breaking changes introduce `/api/v2`; contracts
live in `Muster.Contracts` so clients and connectors share definitions.
