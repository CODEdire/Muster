# Guild shops — build plan / handoff

**Goal:** add **guild-owned shops** alongside the existing member-owned ones, mirroring the quest Guild/Player
origin split. A guild shop is the economic mirror of a guild quest: a guild quest **mints** coins (a source); a
guild shop **consumes** coins (a sink). When a buyer purchases from a guild store the coins are **burned** (removed
from circulation) instead of paid to a member seller.

## Locked decisions

1. **Consume = burn.** A guild-store sale settles by burning the buyer's payment to the burn sink
   (`BurnAccountUserId`) — no member seller is paid. Refunds (cancel/dispute) still return to the buyer.
2. **ShopManager only.** Creating + managing a guild store (and its listings) requires the `ShopManager` tier
   (member stores stay `ShopCreator`). Mirrors guild quests needing a Quest Manager.
3. **No ratings, no offers** on guild stores. There's no member seller to rate, and pricing is fixed (buy-now only).
4. **Default Member.** Add a store `Origin` defaulting to `Member`; backfill all existing stores as `Member`.

## Model

- `ShopStoreOrigin { Member, Guild }` (new enum; default `Member`), mirroring `QuestOrigin`.
- `ShopStore.Origin` — the store kind. A guild store's `OwnerId` is the creating manager (for attribution/audit);
  `Origin` governs behaviour, not the owner.
- `ShopOrder.Origin` — snapshotted from the store/listing at purchase, so settlement can branch even if the listing
  is later deleted (like `ItemNameSnapshot` / `SellerId`). `SellerId` stays the store owner for display
  ("Sold by the guild" when `Origin == Guild`).

## Settlement

- New `CurrencyService.ShopConsumeAsync(guildId, currencyId, amount, sourceKey)` (or an `Origin`/`consume` branch in
  the settle path): escrow → `BurnAccountUserId` for the **full** amount. No seller leg, no commission/fee leg.
- `ShopService` settle paths (`SettleOrderAsync`, dispute resolution `pay-seller`, `AutoSettleDueAsync`) branch on
  `order.Origin`: Guild → consume/burn; Member → existing `ShopSettleAsync` (pay seller, burn fee).
- Refund path (`ShopRefundAsync`) is unchanged — buyer always refundable.

## Authorization (`ShopAuthorizer`)

Apply the quest Guild-vs-Member split:
- `ManageStore` / `CreateListing` / `EditListing` / `CancelListing` on a **guild** store → `ShopManager`.
- The same on a **member** store → owner + `ShopCreator` (unchanged).
- `Purchase` → any participant, including for guild stores (a manager buying from the guild store is fine — coins
  still burn). The self-purchase guard doesn't apply (there's no member seller).
- `MakeOffer` / `Rate` → refused for guild-store listings/orders (decision 3).

## Settings / gating

- Reuse the Shop feature gate (`PlatformFeature.Shop`). No new per-guild toggle for now (manager-gated creation is
  the control). Optionally add `GuildShopSettings.GuildStoresEnabled` later if guilds want to disable the concept.

## Persistence

- Migration `AddShopStoreOrigin`: add `Origin` (int) to `ShopStores` and `ShopOrders`, default `0` (Member). Existing
  rows backfill to Member by the default. No data move needed.

## Web + bot

- **Store create** (web form + `/shop store create`): a "Guild store" option, shown/allowed only to `ShopManager`.
- **Market / storefront**: a **Guild** chip on guild stores + their listings (like the guild-quest chip). "Sold by
  the guild" in place of the seller `UserChip`.
- **Order / receipt**: seller shown as the guild; rating UI suppressed for guild orders.
- **Listing detail**: hide the offer/"make offer" control for guild listings.

## Phases (suggested)

1. Domain + enum + migration + settlement (`ShopConsumeAsync` + settle branch) + authorizer split. Tests to green.
2. Web: store-create guild option (manager-gated), guild chip on market/storefront, "sold by guild" + suppressed
   rating/offer UI.
3. Bot: `/shop store create` guild flag (manager-gated) + guild chip on shop cards.
