# Currency — Wallet/Ledger Hardening + External Connectors

> **Goal of this branch.** Solidify the currency system as a robust **wallet + ledger** that works both
> **internally** (Muster owns the balance) and **with external systems** (the "Coin" loot/economy hook), via
> pluggable **outbound connectors** (Webhook + HTTP API) on the durable outbox, plus the **inbound** reconciliation
> path. The member-facing **shop/store is out of scope** (wishlist).

Severity: 🔴 correctness/security · 🟠 functional gap · 🟡 polish · ❓ decision.

---

## 0. Current state (inventory)

Already built and solid — we extend, not rebuild:

- **Domain.** `Currency { Id, GuildId, Code, Name, IsSeasonal, IsSpendable, Mode }`; `CurrencyMode {Internal,External,Hybrid}`. Append-only `CurrencyLedgerEntry` (idempotent on `(SourceType, SourceId)` unique, filtered); `Wallet` = display cache (rebuildable from ledger). `CurrencyLedgerSource { TrackingSession, Quest, Muster, ManualAward, Connector, Event, Transfer, Adjustment, Checkpoint }`. Entities live one-per-aggregate under `Muster.Domain.Entities.<Feature>` (Currencies/Quests/Events/…). Feature code lives under `Services.Currencies` / `Commands.Currencies`; queries are `CurrencyLedgerQueries` / `CurrencyQueries` extensions.

### Balance authority (hybrid) + history pruning

- **Two read paths.** The ledger `SUM` (`db.BalanceAsync`) is the **transaction authority** — overdraft/spend/transfer decisions sum the ledger, so a stale cache can never permit an overdraft. The `Wallet.Balance` **cache** serves cheap **display** reads (dashboard, `/currency balance`, leaderboard via `TopWalletBalancesAsync` / `WalletBalancesAsync`); it's kept in lock-step on every `StageAsync` and rebuildable, can drift harmlessly, and self-heals on `RebuildWalletsAsync`. **No rowversion** — append-only inserts + a live SUM are concurrency-correct by construction.
- **Checkpoint pruning.** `LedgerPruneService` folds every ledger row beyond the retention window into one carry-forward `Checkpoint` entry per `(user, currency, season)` (Amount = sum of folded rows), then deletes the detail — so the SUM (and balances) are unchanged and the SUM stays bounded to `window + checkpoints`. Composable (a later fold absorbs an earlier checkpoint); re-runs are idempotent (a lone checkpoint is skipped). Two triggers:
  - **Time-based:** daily `LedgerPruneScheduler` (Bot) → `PruneAllAsync`, folding rows older than each guild's *effective* window.
  - **Season-based:** `SeasonService` archive → `CheckpointSeasonAsync` folds that whole season's history into checkpoints immediately (balances preserved for the archived season).
- **Retention policy = guild ⊕ platform.** Per-guild `GuildSettings.LedgerRetentionDays` (0 = inherit) combines with a platform `Currency:MaxLedgerRetentionDays` (AppSettings, 0 = unlimited) via `LedgerRetention.Effective` = "global is both cap **and** default": `guild==0 ? global : min(guild, global)`; `0` only when neither limits. The per-guild value is set + validated against the cap in **web** (`ConfigAdmin`) and **Discord** (`/config-ledger-retention`); `LedgerRetention.ExceedsCap` is the shared validation rule.
- **Service.** `CurrencyService` is the single money path: `StageAsync` (one ledger write, publishes `CurrencyMovementRecorded`, idempotent, season-scoped) → Mint/Spend/Award/AwardPoints/Stage + escrow Hold/Payout/Refund. **Spend overdraft-checked unless `Mode == External`.**
- **CQRS.** Machine-inbound `MintCurrency`/`SpendCurrency` → `CurrencyChangeResult`; user/staff `TransferCurrency`/`AdjustCurrency` (`IGuildCommand`) → `Result` (authorized + audited). In-app earning (quests/musters/events/tracking) awards directly through `ICurrencyService`. Contracts split by feature: `CurrencyMessages.cs` / `QuestMessages.cs` / `Commands.cs` (the `IGuildCommand` marker). *(The old broker-agnostic `AwardCurrency`/`MemberParticipated`/`AdjustCurrencyBalance` → `LedgerEntryRecorded` path was retired — unused; `CurrencyMovementRecorded` is the single money-moved signal.)*
- **Events.** `CurrencyService` publishes **`CurrencyMovementRecorded`** via `IMessageBus` from the single `StageAsync` funnel (covers every path) — the transactional-outbox successor to the retired in-process `ICurrencyEventSink`. `CurrencyMovementRecordedHandler` is the default logging seam; the external connector consumes it later.
- **API.** `POST …/currencies/{code}/mint|spend` (`write:currency`), `GET …/wallets|ledger|leaderboard` (read scopes).
- **Web.** Admin `Currencies.razor` (create/edit Code/Name/Seasonal/Spendable/Mode). Member wallet + ledger history in `MemberPanel`.

**Greenfield:** any connector transport/dispatch, connector config on a currency, inbound idempotency, balance rebuild.

---

## Connector v2 — configurable API client (supersedes the v1 model below)

> v1 (single transport enum + one `BodyTemplate`) shipped first on this branch; **v2 replaces it before merge**
> (no back-compat needed). The connector is now one **HTTP client config**: shared **auth** + orthogonal **signing**
> + independently-configured **actions** (Credit / Debit / GetBalance), with **balance sync** and **encrypted secrets**.

### Model (owned JSON on `Currency.Connector`)

```csharp
public enum ConnectorAuthScheme  { None = 0, Basic = 1, Bearer = 2, ApiKey = 3 } // OAuth = future
public enum ConnectorSignAlgorithm { None = 0, HmacSha256 = 1, HmacSha512 = 2 }
public enum ApiKeyLocation { Header = 0, Query = 1 }

public class CurrencyConnector
{
    public bool   Enabled { get; set; }
    public string? BaseUrl { get; set; }                 // actions' Path may be relative to this, or absolute
    public int    TimeoutSeconds { get; set; } = 10;
    public string? SuccessCodes { get; set; }            // CSV (e.g. "200,201,204"); blank = any 2xx
    public string? IdempotencyHeader { get; set; }       // header carrying the delivery id (receiver dedupes)
    public List<ConnectorHeader> Headers { get; set; } = []; // static extra headers (tenant, version…)
    public ConnectorAuth    Auth    { get; set; } = new();
    public ConnectorSigning Signing { get; set; } = new();
    public ConnectorAction  Credit     { get; set; } = new(); // movement amount > 0
    public ConnectorAction  Debit      { get; set; } = new(); // movement amount < 0 (may be a different endpoint)
    public ConnectorAction  GetBalance { get; set; } = new(); // read external balance for sync
}

public class ConnectorAuth    { ConnectorAuthScheme Scheme; string? Username; string? Secret /*enc*/; string ApiKeyName="X-Api-Key"; ApiKeyLocation ApiKeyIn; }
public class ConnectorSigning { ConnectorSignAlgorithm Algorithm; string SignatureHeader="X-Muster-Signature"; string? TimestampHeader; string? Secret /*enc*/; }
public class ConnectorAction  { bool Enabled; string Method="POST"; string? Path; string? Query; string? BodyTemplate;
                                ConnectorResponseFormat ResponseFormat; string? ResponsePath; } // all 3 actions may send a body + parse a returned balance
public class ConnectorHeader  { string Name; string Value; }
```

- Placeholders in `Path`/`BodyTemplate` (rendered by `ConnectorTemplate`) use `$name` (no braces → the body stays
  valid JSON). Numbers `$userId` `$amount` `$guildId` `$deliveryId` (write quoted, e.g. `"$amount"`, quotes stripped
  on send); JSON-escaped strings `$displayName` `$currencyCode` `$reason` `$sourceType` `$occurredAt`.
- **Query** (templated, URL-encoded) appends to any action's URL; tokens also work in the **path**.
- **Response parsing** on any action (not just GetBalance): `ResponseFormat` = `Json` (read `ResponsePath`, dotted) or
  `Text` (whole body is the number). A value captured from a **Credit/Debit** response is the member's updated
  external balance → reconciles the shadow wallet (wired in balance-sync, §F). GetBalance uses the same mechanism.
- Any action may send a **body** (incl. GetBalance — some APIs read balance via POST).
- Cruor example — Credit/Debit `POST {base}/currency/add-cruor`, ApiKey `x-api-key`, body
  `{ "member_id": "$userId", "display_name": "$displayName", "cruor_amount": "$amount" }`; GetBalance
  `GET {base}/currency/balance/$userId`, `BalancePath` per their response. (Cruor's add-cruor takes negatives, so
  Credit + Debit can point at the same endpoint; other APIs split them.)

### Auth schemes (all secrets encrypted)

`None` · `Basic` (username + password) · `Bearer` (static token; OAuth client-credentials = future connector) ·
`ApiKey` (header or query, configurable name). **Signing is orthogonal** — HMAC-SHA256/512 over `body` (or
`{timestamp}.{body}` when `TimestampHeader` set), so you can sign *and* authenticate.

### Secrets at rest — Data Protection, keys in DB

`Auth.Secret` + `Signing.Secret` are encrypted with ASP.NET **Data Protection** (`IDataProtector`, purpose
`currency-connector`) before persisting; decrypted only when building a request. The key ring is **persisted to the
DB** (`PersistKeysToDbContext<MusterDbContext>`, `DataProtectionKeys` table, shared `SetApplicationName`) so web +
bot + migration hosts all decrypt. Admin UI is write-only (shows *whether* a secret is set; blank on save = keep).

### Outbound dispatch

`LedgerEntryRecordedHandler` (durable outbox): gate on `Mode ∈ {External,Hybrid}` + `Enabled` + `SourceType !=
Connector` (loop guard), pick **Credit** (`amount > 0`) or **Debit** (`amount < 0`), and run it through
`CurrencyConnectorClient` (URL = `BaseUrl`+`Path`, method, static+auth+idempotency+signature headers, rendered body,
timeout, success-code check). Non-success throws → Wolverine retries.

### Balance sync — DONE (slice F)

`CurrencyConnectorSyncService.ReconcileAsync` adjusts a member's shadow wallet to the external balance by posting a
`Connector` ledger entry for the delta (no-op when aligned), then stamps `Wallet.LastSyncedAt`. The external balance
comes from a credit/debit response when it returned one, else a `GetBalance` call. Connector-source entries don't
cascade, so reconciliation never re-enters. Invoked:
- **post credit/debit** — the dispatch handler reconciles after every successful Credit/Debit (uses the returned
  balance, else fetches it);
- **member dashboard** — `MyProfile` reconciles the member's External/Hybrid wallets on visit, throttled to once per
  5 min (`IsDashboardSyncDue`), plus a **Sync balances** button (force);
- **admin "Sync all members"** — publishes `SyncCurrencyBalances`; the handler paces calls (500 ms each) in the
  background (admin is warned it can take a while);
- **periodic sweep** — `CurrencyBalanceSyncScheduler` (bot, 5-min tick) reconciles wallets older than the connector's
  `SyncIntervalMinutes` (0 = off), paced.

*(Pre-spend reconcile was considered but not done: spends on External/Hybrid go through the Connector source, so the
shadow is corrected post-movement + by the throttled dashboard/sweep instead of blocking the spend on a sync call.)*

### Inbound (unchanged from v1)

`mint`/`spend` API + `externalId` idempotency (`sourceId = "connector:{externalId}"`), loop-guarded; `External`
spend skips the overdraft check. *(Kept as built.)*

### Completeness baked in

Per-request **timeout** (a linked CTS; HttpClient.Timeout disabled); **success-code** criteria; **idempotency
header**; **static custom headers**; **query params** (templated) + **path tokens**; per-action **body format**
(JSON or form-urlencoded) and a body on any action incl. GetBalance; **response parsing** (JSON path / plain text /
**regex** capture) on any action, capturing a returned balance; **error-message surfacing** (`ErrorPath` or a body
snippet) + **last-delivery health** (status/error/time) on the admin page; **retry + circuit breaker + concurrency
limiter** via `AddStandardResilienceHandler` (covers sync calls; outbound also retries via the durable outbox, then
Wolverine dead-letters); **secret redaction** (secrets never logged or returned). Per-action **test-send**.

*Deferred (wishlist):* **OAuth2 client-credentials** auth (token fetch/cache/refresh); **secret rotation** (two live
secrets); per-event **filtering**; per-connector **rate-limit tuning** beyond the standard handler's defaults.

---

## 4. 🟡 Wallet/ledger solidification

- **Rebuild.** `RebuildWalletsAsync(guildId, ct)` on the service: recompute every wallet balance from the ledger (safety net + post-import reconcile). Admin-triggered + covered by a test (mutate a wallet, rebuild, assert it matches the ledger sum).
- **Invariants (tests).** balance = Σ ledger per (user,currency,season); idempotent award no-ops; External spend below zero allowed; Internal spend blocked; escrow legs net to zero.
- 🟡 Member/admin wallet view already exists — confirm it shows every currency + recent history (it does). No transfer/decay in scope (wishlist).

---

## 5. 🟡 Admin / UX

- `Currencies.razor`: when `Mode ∈ {External, Hybrid}`, show a **Connector** sub-form (transport, endpoint, secret [write-only], enabled) + a **"Send test"** button that posts a synthetic `ConnectorDispatch` and reports the HTTP result. `CurrencyAdminService.UpdateAsync` extended to persist the connector (secret left unchanged when the field is blank).
- Audit connector edits + test sends.

---

## 6. Tests

- **Transports (unit):** webhook HMAC signature value; HttpApi auth header; payload JSON shape; dispatcher gating (mode/enabled/loop-guard) selects/​skips correctly.
- **Inbound (integration):** mint/spend with `externalId` is idempotent (second call no-ops, balance unchanged); External spend skips overdraft; Internal spend blocks.
- **Rebuild (integration):** wallet drift corrected from the ledger.

---

## 7. Build order (slices)

1. **Domain + persistence** — `ConnectorTransport`, `CurrencyConnector` owned-JSON on `Currency`; migration (+ JSON backfill of `$.Connector`). 
2. **Transports + dispatcher** — `ICurrencyTransport`, Webhook + HttpApi, `CurrencyConnectorDispatchHandler` replacing the stub; DI; loop-guard. Unit tests.
3. **Inbound idempotency** — `externalId` on API + `MintCurrency`/`SpendCurrency` + handlers. Integration tests.
4. **Wallet rebuild** — service method + admin trigger + test.
5. **Admin connector UI** — config sub-form + test-send + audit.
6. **Docs** — `api.md` (connector payload + idempotency), `Features.md` (check off outbound connectors), `gotchas.md` (secret at rest).

## v3 — Consolidated synchronous funnel (LOCKED decisions)

One `ICurrencyService` owns the whole operation — transaction + ledger + the external call. For External/Hybrid
currencies the connector is called **synchronously, and must succeed before we finalize**; on failure the operation
**aborts** (nothing committed — including a quest payout, which can therefore fail if the external API is down). On a
post-success local-commit failure or a transfer's second-leg failure, we **don't auto-reverse**: log, flag connector
health for admin attention, and let the GetBalance sweep reconcile the shadow. (Replaces the v2 async outbox-push.)

**Mechanism — gate the external call in `CurrencyService.StageAsync`** (the single leg-writer, so *every* path —
quest award/escrow, mint, spend, transfer, manual award — is covered by one funnel):
- Push when: `currency.Mode ∈ {External, Hybrid}` **and** connector `Enabled` **and** `userId != EscrowAccountUserId`
  (house legs are internal) **and** `sourceType != Connector` (Connector = external-origin → skip, no echo).
- Sign picks the action: `amount > 0` → Credit, `< 0` → Debit. On `!result.Success` → throw → the operation rolls
  back before commit. Multi-leg ops (transfer, escrow) call external per member leg; a later leg failing after an
  earlier external success can't auto-reverse → flag + reconcile.
- **Caveat:** the external HTTP call happens inside the open DB transaction (external-before-commit) — acceptable at
  currency volumes; noted. Idempotency of the synchronous call (vs resilience-handler retries) is a follow-up.
- The v2 `LedgerEntryRecorded → connector` async dispatch is **retired** (the connector client/auth/signing/actions
  are reused by the synchronous call); the GetBalance **sweep + reconcile stay** as the drift safety net.

**CQRS funnel (as built — refined from the original plan):** two distinct paths, because "machine mirrors an
external movement" and "a user/staff moves money" have opposite external semantics:
- **Machine inbound** — `MintCurrency`/`SpendCurrency` (with `ExternalId`) stay as-is: scope-gated (`write:currency`),
  `SourceType = Connector` (so they're loop-guarded and **never pushed back out**), return `CurrencyChangeResult`.
  These mirror what an external economy already did. **Not** `IGuildCommand` (an unbound machine key has no actor to
  authorize, and authorizing inbound mirrors as a member would be wrong).
- **User/staff CQRS** — `TransferCurrency` + `AdjustCurrency` are `IGuildCommand` → `ICurrencyAuthorizer` →
  `ICurrencyService` → `Result` (audited by `AuditMiddleware`). Members transfer only their own wallet; economy staff
  (officers + admins) mint/adjust/move anyone's. `AdjustCurrency` folds staff "mint" (Δ>0) and "adjust" (Δ≠0):
  `SourceType = Adjustment`; `TransferCurrency` writes two `SourceType = Transfer` legs (`:out`/`:in`) that **do**
  push externally. `CurrencyPermission {View, Spend, Transfer, Mint, Adjust}`.

Adapters: bot `/currency` (`balance`/`give`/`mint`/`adjust`), API (`transfer`/`adjust` actor-bound, plus
`GET /currencies` and `…/currencies/{code}/balance`). Balance is read back after a successful `Result` (keeps the
handler return uniform for auditing).

**Build order:** ✅ v3-1 sync external in `StageAsync` + retire async push + flag → ✅ v3-2 `ICurrencyAuthorizer` →
✅ v3-3 `TransferCurrency` → ✅ v3-4 staff `AdjustCurrency` (`IGuildCommand`+`Result`+audit) → ✅ v3-5 endpoints +
`/currency` Discord → ✅ v3-6 docs. **DONE** (216 integration + 60 unit + 35 persistence green).

## 🔴 Foundational finding — connector only sees *some* movements (resolved by v3)

`LedgerEntryRecorded` (the connector dispatch trigger) is published **only** by the broker-agnostic award command
handlers (`AwardCurrency` / `MemberParticipated` / `AdjustCurrencyBalance`, which cascade it). Every other ledger
write — **quest rewards/escrow** (`QuestService` → `ICurrencyService.AwardAsync/PayoutAsync` directly), **API
mint/spend**, direct awards, and the planned **Transfer** — commits the ledger **without** publishing it, so those
movements **never reach the connector**. For an External/Hybrid currency a quest payout in COIN won't push outbound.

`CurrencyService.StageAsync` already emits a `CurrencyMovement` via `ICurrencyEventSink` for **every** leg — the
universal signal — but it fires pre-save (no ledger id yet), and the default sink only logs.

**Fix (recommended): an EF `SaveChanges` interceptor** publishes `LedgerEntryRecorded` (with the real id) for every
inserted `LedgerEntry`, enlisted in the Wolverine outbox so it commits with the write. Then *all* paths
(quest/mint/spend/transfer/award) reach the connector uniformly, and the award handlers stop hand-cascading it. This
is the foundation the CQRS-parity slice below should sit on.

## CQRS parity roadmap (toward the GuildQuest pattern)

GuildQuest is the reference architecture; currency should converge on it so other systems (quests, events, bot, web,
API, external connectors) act against currency through one audited, authorized funnel.

**GuildQuest pattern (reference):** commands are `IGuildCommand` (GuildId + **ActorId**) → thin handlers (load →
`IQuestAuthorizer.AuthorizeAsync(actor, resource, permission)` → service → `Result`/`Result<T>`); **`AuditMiddleware`**
is auto-attached to every `IGuildCommand` chain (actor from the command); every surface invokes the *same* command via
`IMessageBus` — Discord `/quest` subcommand tree, Wolverine.HTTP `[RequireApiScope(…, requireActor)]` endpoints
(`http.ApiActor()`), web; transitions publish lifecycle events over the durable SQL queue for the bot.

**Currency today (gaps):**
- Commands `MintCurrency`/`SpendCurrency`/`AwardCurrency`/`AdjustCurrencyBalance` are **not `IGuildCommand`** → carry
  **no actor** and are **not audited** by `AuditMiddleware`. Result is the ad-hoc `CurrencyChangeResult`, not `Result`.
- **No `ICurrencyAuthorizer`** — mutation auth is only the API `write:currency` scope + admin-page gating; no per-actor
  rule (who may mint vs spend vs adjust vs transfer), no reuse for web button-visibility.
- Endpoints: reads + mint/spend only — **no transfer, no adjust, no single-balance, no list-currencies**.
- Discord currency surface (`/award`, `/wallet`, `/leaderboard`) isn't a unified bus-dispatched tree.

**Proposed additions (ordered):**
1. 🟠 **`TransferCurrency(GuildId, ActorId, FromUserId, ToUserId, Code, Amount, Reason)`** — debit source + credit
   destination **atomically** (one txn, two staged ledger legs via a new `CurrencyService.TransferAsync`), overdraft-
   checked on the source (skipped for External), authorized (actor == source, or admin force-move). Controls **both**
   ledger sides. + `POST …/currencies/{code}/transfer` + Discord `/currency give`.
2. 🟠 **Currency commands become `IGuildCommand`** (add `ActorId`) → free `AuditMiddleware` audit + uniform actor; align
   handlers on the `Result` envelope. Lets quests/events/other features invoke currency actions through the same
   audited funnel instead of only direct `ICurrencyService` calls.
3. 🟠 **`ICurrencyAuthorizer`** (`GuildActor` + `CurrencyPermission {View, Spend, Transfer, Mint, Adjust}`) — members
   spend/transfer their own; managers/admins mint/adjust; reused by handlers + UI. Discord `RequiredRole` as
   defense-in-depth, mirroring quests.
4. 🟡 **Round out endpoints**: `transfer`, admin `adjust`, `GET …/currencies` (list), `GET …/currencies/{code}/balance/{userId}`.
5. 🟡 **Unified Discord `/currency`** (balance / give / — admin: mint, adjust), all via `IMessageBus`.

## Surface parity & UX roadmap (Discord / Web / API)

**Goal:** every way to *experience* a currency is reachable on each surface where it makes sense.
**Parity model (LOCKED):** *capability parity with surface-appropriate depth* — money **operations**
(balance, history, transfer, mint, adjust, leaderboard) behave identically (same semantics + authorization)
on all three surfaces; **management/config** (currency CRUD, connector, retention, API keys, analytics) is
**Web-primary**, with a glanceable/quick subset on Discord and an optional thin API. No multi-field config in
a slash command.

### Audiences
- **Members** value speed + transparency + agency: *what do I have, how did I get/spend it, send some, where
  do I rank, what is this currency for.* They live in Discord + the member web view; rarely see config.
- **Staff/admins** value control + auditability + integration + insight: grant/correct accountably, define
  currencies, wire external economies, monitor health/supply, reconcile, see who-did-what. They live in Admin.
- **Integrations** value programmatic read/write parity + events out.

### Capability × surface matrix (✅ have · 🟡 partial · ⬜ gap)

**Member**
| Capability | Discord | Web | API |
| --- | --- | --- | --- |
| My balances (all) | ✅ `/currency balance` | ✅ `/me` | ✅ `…/wallets` |
| My transaction history | ✅ `/currency history` *(MS‑2)* | ✅ MemberPanel | ✅ per-member ledger *(MS‑1)* |
| Transfer/give to a member | ✅ `/currency give` | ✅ Wallet Send *(MS‑3)* | ✅ `…/transfer` |
| Leaderboard | ✅ `/leaderboard` (POINTS) | ✅ dashboard + currency selector *(MS‑3)* | ✅ any currency `?currency=` *(MS‑1)* |
| Currency directory ("what exists / for what") | ✅ `/currency list` *(MS‑2)* | 🟡 admin list only | ✅ `…/currencies` |
| Receipt / notification (got / sent) | ✅ DM on grant + `/currency notify on\|off` *(MS‑4)* | ✅ Wallet receipts toggle *(MS‑4)* | (movement event exists internally) |
| Spend / redeem (shop) | ⬜ | ⬜ | ⬜ *(wishlist)* |

**Admin / staff**
| Capability | Discord | Web | API |
| --- | --- | --- | --- |
| Mint to a member | ✅ `/currency mint` | ✅ Award console | ✅ `…/adjust` |
| Adjust (+/-) | ✅ `/currency adjust` | 🟡 console framed positive-only | ✅ `…/adjust` |
| Bulk award/adjust (many members) | ⬜ | ⬜ | ⬜ *(removed with ManualAward)* |
| View any member's balances + ledger | ⬜ | ✅ MemberDetail | 🟡 single balance / guild-wide ledger |
| Create / edit currency | ⬜ | ✅ Currencies + edit | ⬜ (list only) |
| Connector config + test + health | ⬜ | ✅ CurrencyEdit | ⬜ |
| Retention config | ✅ `/config-ledger-retention` | ✅ Role-mapping page | ⬜ |
| Sync member / sync-all / reconcile | ⬜ | 🟡 self-sync + admin sync-all | ⬜ |
| Rebuild wallets | ⬜ | 🟡 Currencies page | ⬜ |
| Currency audit (who minted/adjusted/sent) | ⬜ | ✅ Audit console | 🟡 not exposed |
| Supply / analytics (total minted, top holders, velocity) | ⬜ | ⬜ | ⬜ **gap** |
| API key management | ⬜ | ✅ ApiClients | ⬜ |

**Integration**
| Capability | API |
| --- | --- |
| Inbound mint/spend (machine mirror) | ✅ |
| Outbound movement webhooks | ⬜ — `CurrencyMovementRecorded` published internally only **gap** |

### Placement principles
- **Discord** = glance + quick single-target verbs (`balance`, `history`, `give`, `mint`, `adjust`,
  `leaderboard`, `list`/directory). Ephemeral. No connector/bulk/CRUD.
- **Web** = management system of record + rich member self-service (Wallet page, standalone Leaderboard,
  Admin → Currencies suite, bulk console, audit, analytics, API keys).
- **API** = read parity (balances, **per-member ledger**, leaderboard, currencies) + money ops + (later)
  outbound webhooks + optional config-write.

### Themed gaps
1. **Member self-service depth** — no Discord `history`/`list`; no web *send* form; leaderboard not its own page.
2. **Admin reach on Discord** — can't view a member's wallet/ledger or run sync from Discord.
3. **Bulk operations** — bulk award/adjust gone; wanted for events/payouts.
4. **Analytics/insight** — no per-currency supply / top-holders / movement feed anywhere (admins' biggest blind spot).
5. **Receipts/notifications** — ✅ DM on deliberate grants received (`/currency notify` + web toggle to opt out). *(MS‑4)*
6. **API symmetry** — per-member ledger, audit read, outbound event webhooks.

---

### Phase 1 — Member self-service (chosen first)

> Money verbs land on Discord + Web + API with identical authz; nothing here adds config to Discord.

- **MS‑1 · Read foundation.** ✅ **DONE.** `ICurrencyReadService.GetMemberHistoryAsync(guild, user, code?, skip, take)`
  (new per-member paged `CurrencyLedgerQueries.MemberLedgerAsync`, code-resolved `LedgerHistoryEntry` rows) +
  `GetLeaderboardAsync(guild, code, top)` for **any** currency (POINTS/season default kept). **API:**
  `GET …/members/{userId}/ledger?currency=&skip=&take=` + `?currency=` on the leaderboard endpoint. Read parity complete.
- **MS‑2 · Discord depth.** ✅ **DONE.** `/currency history [currency] [count]` (ephemeral, caller's recent entries
  with Discord relative timestamps) + `/currency list` (directory: code/name + spendable·seasonal flags), both via
  `ICurrencyReadService` (`GetMemberHistoryAsync` / `GetCurrenciesAsync`).
- **MS‑3 · Web member Wallet.** ✅ **DONE.** `/me` reworked into the member **Wallet** (balances + currency-filtered
  history + inline **Send** form dispatching `TransferCurrency`). The existing dashboard-leaderboard gained a
  **currency selector** (season POINTS default → any currency) instead of a new page. Sidebar gains **Leaderboard** +
  **Wallet** items; `WebMemberService` history rides `ICurrencyReadService` (deduped) + `GetRecipientsAsync`
  (humans, minus self/bots).
- **MS‑4 · Receipts / notifications.** ✅ **DONE.** `CurrencyMovementRecorded` is routed cross-host over a durable
  SQL queue (`WolverineExtensions.CurrencyEventsQueue` = `"currency-events"`, mirroring the quest-board queue) so a
  grant issued from web/API still reaches the bot. The bot's **`CurrencyDmHandler`** DMs the recipient on **deliberate
  grants only** — `ShouldNotify` = positive amount **and** real member (not the `EscrowAccountUserId` house account)
  **and** source ∈ {`Transfer`, `Adjustment`, `ManualAward`}. Earning sources (Quest/Muster/Event/TrackingSession),
  Connector echoes, and Checkpoint folds are excluded (the firehose would be noise; pruning checkpoints bypass
  `StageAsync` so they never publish). **Control:** per-user opt-out (default = receipts on) via
  `DiscordUser.CurrencyDmOptOut` — toggled by **`/currency notify on|off`** or the web **Wallet → Notifications**
  checkbox (`MembershipQueries.{CurrencyDmOptOut,SetCurrencyDmOptOut}Async`). Best-effort delivery (closed DMs no-op).

### Later phases (sketched, not scheduled)
- **Phase 2 — Admin depth:** bulk award/adjust; admin member-lookup + sync from Discord; **Currency overview /
  analytics** (supply, top holders, recent-movement feed); reframe the web console to mint **and** adjust.
- **Phase 3 — API parity + events:** per-member ledger (from MS‑1), audit read, **outbound movement webhooks**
  (subscribe to `CurrencyMovementRecorded`), optional config-write.

## Wishlist (deferred)

- 🟡 **Member shop/store** — admin-defined catalog (item, cost, currency, stock, fulfillment = role grant / manual / connector); members redeem → spend debits the ledger. The headline member-facing spend surface; deferred by decision.
- 🟡 **Discord message-command transport** — for economy bots that only accept prefix/message commands.
- 🟡 **OAuth2 client-credentials** connector auth (token fetch/cache/refresh) + **secret rotation** (two live secrets).
- 🟡 **Decay / expiry** of currency over time.
- 🟡 **Decimal/fractional balances** — the ledger is `long` (whole units); a fractional economy would truncate. Needs a scale/precision model.
- 🟡 **Reconcile sanity guard** — clamp/alert on a huge sync delta (external reset / bad parse) instead of adjusting blindly.
- 🟡 **Post-credit reconcile race (Hybrid)** — a sync right after a mint can briefly under-count until the external side applies the push; the next sync corrects it. Tighten if it bites.
- 🟡 **First-enable backfill** — auto-run a full member sync when a connector is first enabled (today the admin clicks "Sync all").
- 🟡 **Inbound push (real-time)** — a signed inbound webhook ("balance changed") instead of polling; revisit with the broader **webhooks** work.
