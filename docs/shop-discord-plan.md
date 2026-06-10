# Shop on Discord — design + plan

How the player marketplace comes to Discord. Web + API are already complete; this is the
Discord build. Linked from [capability-matrix.md](capability-matrix.md).

_Status: design locked 2026-06-05; not yet built._

## Core principle

The quest/muster "one persistent card per entity in a channel" pattern **does not fit a
marketplace**. Quests/musters are low-volume, time-bound events; shop listings are
high-volume browsable inventory. A Discord channel is a linear append-only log — it can't
sort/filter and floods instantly. So:

> **Browse lives behind a command (ephemeral). The channel holds only curated content.**

**Revision (2026-06-06):** the channel now also holds a persistent **per-store home card** (one per
open store — the shop's representation in Discord, with a "Browse the shop" button), in addition to
featured listing cards. This supersedes the original "no persistent home card" decision. Both card
types live in `ShopBoardNotificationHandler`; `ResyncShopChannel` / `ResyncShopStore` (auto on
channel-link/change + manual buttons) guarantee a card exists.

## Decisions (locked)

- **Browse = ephemeral `/shop` hub.** Per-user, paginated, filterable, sortable. This is
  the entry point. The "home" is this ephemeral landing screen — **not** a pinned Discord
  message, and no persistent home message is maintained in the channel.
- **Featured listings = the only channel cards.** A seller pays to feature a listing; it
  posts a card to `ShopChannelId` and sorts to the top of ephemeral browse.
  - Fee: **global guild setting, 0 allowed (free)**.
  - Fee destination: **burn** (debit seller, credit the burn sink — consistent with the
    commission burn; ledger source `ShopFee`).
  - Cap: **max 5 featured listings per store**.
- **New-listing notifications: none for now.** No per-listing channel posts, no follow/
  wishlist DMs, no digest. Revisit later.
- **Async order events = targeted DMs** (use the `TargetUserId` already on
  `ShopLifecycleNotified`), plus an ephemeral "My orders" view.
- **Dispute alert** stays in the mod channel but upgrades from plain text to a rich embed
  with arbitrate buttons.

## Net channel volume

| Surface | Content | Volume |
|---|---|---|
| Channel (persistent) | capped featured cards only (≤ 5/store, gated by fee) | small, self-limiting |
| `/shop` ephemeral | full sorted/filtered catalog + transact | per-user, invisible to others |
| DMs | your orders, offers | targeted |
| Mod channel | dispute embeds | rare |

## Phases

### Phase A — Ephemeral browse hub ✅ (built 2026-06-05)
`/shop browse` opens an ephemeral, per-user catalog:
- featured row at top, then paginated listings
- filter (category / store) + sort selects, prev/next buttons
- pick an item → detail view with **Buy** / **Make offer** buttons
- "My orders" button → ephemeral order list
New: `ShopEmbedRenderer`, `ShopComponentBuilder`, `ShopInteractionModule`. No channel posts.
This is the bulk of the work and is shippable on its own.

### Phase B — Transact + order management ✅ (built 2026-06-05)
Wire every action button/modal to the existing CQRS commands (same funnel as web/API):
buy, make-offer (modal), accept/counter(modal)/decline, confirm-receipt, mark-delivered,
seller-cancel, dispute (modal reason), rate (stars select + comment modal), arbitrate
(manager: pay-seller / refund-buyer). custom_id carries ids
(`sbuy:{guild}:{listing}`, `sconf:{guild}:{order}`), stateless, authorize on click.
Add `ShopDmPushHandler` for async events (Purchased/Sold/Offer*/Settled/Refunded/Rated),
keyed on `TargetUserId`. Upgrade the dispute mod alert to a rich embed + arbitrate buttons.

### Phase C — Featured listings ✅ (built 2026-06-05)
- New seller action: `/shop feature <listing>` (and a "Feature" button on the
  seller's own listing detail).
- Charge the global fee in the listing's currency → burn (ledger source `ShopFee`,
  idempotent key per feature). 0 fee = free.
- Enforce **≤ 5 featured per store**; reject over cap.
- Post a featured card to `ShopChannelId`; sort-to-top in ephemeral browse.
- Expiry sweep removes the card when the feature window lapses or the item sells/cancels
  (extend `ShopSweepScheduler` + a board-cleanup pass like the muster one).
- New settings on `GuildShopSettings`: `FeaturedListingFee` (long, 0 = free),
  `FeaturedDurationHours` (int), `MaxFeaturedPerStore` (int, default 5). Reuse `ShopChannelId`.
- **Card resync** (`ResyncShopChannel`): re-posts every currently-featured card to the board
  channel. Fires automatically when the shop channel is first linked / changed (so cards that
  were featured before the channel existed appear), and is exposed as a manual button
  (web admin settings + store config) / `/shop resync` / API `POST shop/resync`. Editing a
  listing also republishes so a featured card never goes stale.

### Deferred
- Follow/wishlist DMs + new-listing digest.
- Featured-slot **auction** (Cruor mock already exposes auction endpoints).
- Cross-guild market, full-text search, anti-wash-trading heuristics.

## Phase C specifics (locked)
- Featured fee charged in the **listing's own currency**, burned (ledger source `ShopFee`).
- Feature duration is **fixed by a global guild setting** (`FeaturedDurationHours`), not a
  per-feature command arg.
- Cap is **per-store only** (`MaxFeaturedPerStore`, default 5) — no guild-wide ceiling.
