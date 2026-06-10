# Capability Matrix — Web / API / Discord

Snapshot of what each feature exposes across the three surfaces, what generates Discord
messages, and the known gaps. Living document — review + extend over time.

_Last updated: 2026-06-05 (after Shop Phase 6 ratings)._

Legend: ✅ full · ◑ partial · ✖ none

## Feature × surface

| Feature | Web UI | REST API | Discord bot |
|---|---|---|---|
| **Shop — browse / stores / listings** | ✅ | ✅ | ✅ `browse [store]` (scope+sort hub) · `stores [mine]` (directory) · `search` · per-store **hero card** (featured inline) · `store-create/edit` · `list-item` (cat/qty/tags/expiry) · `listing-edit/cancel` · `feature` |
| **Shop — buy / offer / counter / dispute / arbitrate** | ✅ | ✅ | ✅ (browse buttons + My-orders + DMs + dispute embed) |
| **Shop — ratings (rate / moderate / view)** | ✅ | ✅ | ✅ (rate on settled order; moderate via web/API) |
| **Shop — featured listings (paid, burned)** | ✅ | ✅ | ✅ `feature` + channel cards |
| **Shop — settings / categories / store-types admin** | ✅ (Admin: categories + store-types pages) | ✅ category + store-type CRUD, store delete, settings read (write web-only) | ◑ `category-create`, `storetype-create` |
| **Quests — full lifecycle** | ✅ | ✅ | ✅ (cards + DMs + buttons + modals) |
| **Musters — check-in lifecycle** | ✅ | ✖ **no API** | ✅ (card + button + config) |
| **Tracking / Sessions** | ✅ | ✅ | ✅ |
| **Multipliers / monitored channels** | ✅ | ✅ | ✅ |
| **Events / Ops** | ✖ **no web page** | ✖ | ✅ `/op` only |
| **Currency: balance / transfer / history** | ✅ | ✅ | ✅ |
| **Currency: mint / adjust / spend** | ✅ (Econ mgr) | ✅ | ✅ |
| **Currency: create / edit currency** | ✅ | ◑ list-only read | ✖ (only `config-session-coin`) |
| **Leaderboard** | ✅ | ✅ | ✅ |
| **Seasons start / end** | ✅ | ✖ | ✅ |
| **Role→tier mapping / config** | ✅ | ✖ | ✅ `/config-*` |
| **Member directory / detail** | ✅ | ◑ per-member wallet/ledger/tracking; no roster/roles | ✅ `/syncmembers` |
| **Webhooks / connectors** | ✅ | ✖ | ✖ |
| **API clients (key issuance)** | ✅ | ✖ | ✖ |
| **Audit log** | ✅ + CSV | ✅ `read:audit` | ✖ |
| **Personal: timezone / DM prefs** | ✅ | ◑ tracking-privacy only | ✅ `/timezone`, `/currency notify`, `/track privacy` |
| **CSV exports (participation / audit / session)** | ✅ cookie-auth | — | ✖ |

## What generates Discord cards / messages

Only **three** features push to Discord today. All ride the Wolverine cross-host bus
(domain event published from any host → bot host renders).

| Source | Trigger | Output | Where |
|---|---|---|---|
| **Quest board card** | `QuestLifecycleNotified` (every state) | rich embed + phase-aware buttons; moves public↔mod channel; pruned after retention | channel |
| **Quest DMs** | Claimed/Submitted/RevisionRequested (action cards); Settled/Rejected/Refunded/Released/Reopened (outcome notices) | embed ±buttons, best-effort | DM |
| **Quest deadline reminders** | 15-min scheduler, `DeadlineReminderHours` | embed | DM |
| **Muster card** | `MusterChanged` (create/check-in/close/edit) | live-roster embed + Check-In button; status colors; pruned after retention | channel |
| **Currency receipt** | `CurrencyMovementRecorded` (Transfer/Adjustment/ManualAward only) | embed, opt-out-able | DM |
| **Shop hero card** | `ShopStoreChanged` + store's listing changes | one card per open store: logo/owner/rating/item-count + featured items inline (quick-pick select) + Browse button | shop channel |
| **Shop order DMs** | `ShopLifecycleNotified` w/ TargetUserId | action cards (purchased/delivered/offer*) + outcome notices (settled/refunded/rated…) | DM |
| **Shop dispute alert** | `ShopLifecycleNotified(Disputed)` | rich order embed + arbitrate buttons | mod channel |

Background posters: quest/muster board cleanup (5 min), muster expiry (1 min), quest
reminder (15 min). Shop sweep (1 min) settles/reveals/expires but posts nothing.

## Gaps

1. ~~**Shop is barely on Discord.**~~ ✅ RESOLVED (2026-06-05) — ephemeral browse hub,
   order management buttons + DMs, rich dispute embed, and paid featured channel cards
   (`ShopChannelId` now drives them). See [shop-discord-plan.md](shop-discord-plan.md).
2. **Musters have no API.** Web + Discord only; every other major feature has REST.
3. **Events/Ops only exist in Discord `/op`.** No web page, no API.
4. **Admin/config has no API surface.** Seasons, role mapping, currency create/edit,
   webhooks, API-client issuance — web (and some bot) only. May be intentional (admin =
   human), but call it consciously.
5. **Ratings not on Discord.** Web + API only; no bot rendering of reputation or rate prompt.
6. **Member management API thin.** Per-member reads exist; no roster list, no role/tier
   assignment via API.
7. **Personal-prefs API gap.** Timezone + DM opt-out settable in web/bot but not API
   (only tracking-privacy is).

**Product-level (plan wishlist, not built):** auctions/bidding, wishlists/follows +
outbid alerts, cross-guild market, full-text search, anti-wash-trading velocity heuristics.

## Plans

- [shop-discord-plan.md](shop-discord-plan.md) — bringing Shop to Discord (ephemeral browse
  hub + paid featured listings + order DMs). Web/API already complete.

## Source map

- Web pages: `src/Muster.Web/Components/Pages/**`
- API: `src/Muster.Web/Api/Api*Endpoints.cs` (scopes in `RequireApiScope.cs`, auth in `ApiAuth.cs`/`ApiReadGuards.cs`)
- Bot commands: `src/Muster.Bot/**/Modules/*`, interactions in `**/Modules/*InteractionModule.cs`
- Discord rendering: `src/Muster.Bot/{Questing,Musters}/Rendering/*`, handlers in `**/Handlers/*`
- Cross-host routing: `src/Muster.Contracts/MessageRouting.cs`, `src/Muster.Infrastructure/WolverineExtensions.cs`
