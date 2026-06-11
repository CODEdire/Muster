# Quest Improvements — build plan / handoff

Branch: `feature/quest-improvements` (off `feature/shop`, because it needs the `IFeatureGate` infra +
shop-standard components — `PageHeader`, `FeatureGateNotice`, `dg-toolbar`, autosave/save-pill — that aren't on
`main` yet).

**Goal:** bring the Quest system up to the **Shop standard** (the new overhaul baseline). Use the shop equivalents
as templates throughout. See [MusterShop.md](MusterShop.md) and [feature-gating.md](feature-gating.md).

## Locked decisions

1. **Quest settings → table-per-feature.** Migrate the legacy owned-JSON `Guild.Quests` (`QuestSettings`,
   `OwnsOne` in `MembershipConfigurations`) into a dedicated `GuildQuestSettings` table.
   *Forward migration:* move if present, **persist only to the new table going forward**. Leave the legacy
   `OwnsOne` JSON in place this deploy; drop in a later migration. **Also add an all-guilds backfill at deploy time**
   (in `Muster.MigrationService`) so nothing is left unmigrated before the drop.
2. **Quests wind down like shop orders.** Board + posting/claiming gate on `IsEnabled`; in-flight quests
   (claimed / submitted / awaiting payout) stay reachable on `CanEnable`.
3. **Quest types** — a `QuestType` admin vocab (name + Material icon + sort), mirroring `ShopStoreType`. Seed **12
   defaults**, Material icons (no shipped art): Gathering `grass`, Combat `swords`, Bounty/Hunt `crisis_alert`,
   Delivery/Hauling `local_shipping`, Escort `shield`, Exploration `explore`, Mining `diamond`, Crafting `build`,
   Trade `storefront`, Raid/Group `groups`, Salvage `recycling`, Recovery/Fetch `inventory_2`. The type's icon is the
   **card visual** (quests have no images). Optional uploaded image per type is a later pass.
4. **Detail stays a full page** (not modal) — keep all functionality (claim/submit/participants/review/payout);
   just redesign the header to a `PageHeader` breadcrumb.

## Phases (tracked as tasks #1–#6)

1. **GuildQuestSettings table + forward migration** *(in progress)*
2. **Quest feature gate** — `PlatformFeature.Quests` (flag `MusterQuests`), enforce in `QuestAuthorizer`, hide nav,
   gate pages, wind-down for in-flight quests; bot + API gating like shop.
3. **Quest types** — entity + table + seed (12) + `GuildQuest.QuestTypeId` + admin CRUD (mirror `ShopStoreTypes`) +
   assignment in post/edit + bot autocomplete.
4. **Settings page to shop standard** — `PageHeader`, Accordion, `mform`, debounced autosave + save-pill, master
   **Quests enabled** toggle above the toolbar (greyed + reason when platform-blocked), gated content + disabled
   sub-links. Bind to `GuildQuestSettingsService`.
5. **Browse filter/sort/paging + card** — swap bespoke `.search`/`.sort-by`/`.pager` for the shop `dg-toolbar` kit
   (`dg-filters`/`dg-select`/`dg-search`), filter chips + Clear-all, grid/list view toggle, richer sorts (keep the
   query-param model already in `Quests.razor`). Redesign `quest-card` with shop polish: quest-type icon as the
   visual, `UserChip` poster, tier/status chips, equal-height, hover.
6. **Detail + post/edit headers** — `PageHeader` breadcrumb (Quests › Name); replace the old `page-head` +
   `← back-link` in `QuestDetail`/`QuestPost`/`QuestEdit`.

## Phase 1 detail (do this first)

Templates: `GuildShopSettings` (entity), `GuildShopSettingsService`, `ShopConfigurations` (EF config),
`InfrastructureExtensions` (DI + IOptions defaults), and `GuildShopSettingsService` mapping in the seeded-row path.

1. **Entity** `GuildQuestSettings` (`Muster.Domain.Entities.Guilds`): `GuildId` PK+FK; mirror all `QuestSettings`
   fields — channels (`QuestChannelId`, `QuestModChannelId`), `BoardRetentionHours`, `DeadlineReminderHours`,
   approval (`QuestsRequireApproval`, `PersonalQuestIntakeApproval`, `AllowSelfParticipation`, `FinalApprovalMode`),
   timeouts (`IntakeTimeoutHours`+`IntakeTimeoutAction`, `ClaimTimeoutHours`, `SubmissionTimeoutHours`+
   `SubmissionTimeoutAction`, `FinalApprovalTimeoutHours`+`FinalApprovalTimeoutAction`, `DisputeTimeoutHours`),
   caps (`MaxOpenQuestsPerPoster`, `MaxActiveClaimsPerUser`, `MaxRevisions`), tier points (`TierS..E Points` +
   `PointsForTier`) — **plus a new `bool QuestsEnabled = true`** for the gate. Add a shared
   `static GuildQuestSettings FromLegacy(ulong guildId, QuestSettings src)` mapper.
2. **Service** `GuildQuestSettingsService` (mirror `GuildShopSettingsService`): `GetAsync` returns the row or, on
   miss, seeds it via `FromLegacy(Guild.Quests)` + saves; `UpsertAsync(guildId, Action<GuildQuestSettings>)`.
   IOptions defaults template like shop.
3. **Persistence** — `DbSet<GuildQuestSettings>` in `MusterDbContext`; EF config (1:1 with `Guild`, JSON for any
   list, RowVersion SQL-only) in a `QuestConfigurations`/`MembershipConfigurations`; migration
   `AddGuildQuestSettings`. **Keep** the legacy `OwnsOne(x => x.Quests)`.
4. **All-guild backfill** — a shared `static Task<int> BackfillAsync(MusterDbContext db, CancellationToken)` that
   inserts a `GuildQuestSettings` row (from `FromLegacy`) for every guild lacking one. Call it in
   `src/Muster.MigrationService/Program.cs` **after** `await db.Database.MigrateAsync()`, log the count.
5. **Atomic cutover** — switch every **settings** read/write from `guild.Quests` to the service. ⚠️ Distinguish
   `guild.Quests` (the owned `QuestSettings`) from `db.Quests` (the `GuildQuest` DbSet) — only the former changes.
   Grep `\.Quests\.` field access + `QuestSettings` type usage. Sites to check:
   - Infra: `ConfigCommandService` (the **write** path — `SetQuestAutomationAsync` etc.), `QuestService`,
     `QuestMaintenanceService`, `QuestAuthorizer`, `QuestReadService`, `QuestExtensions`, `QuestCommandHandlers`,
     `QuestSweepScheduler`, `QuestResultPresentation`.
   - Web: `ApiQuestEndpoints`, `QuestActionRunner`, audit `QuestFormatter`, and the settings page (Phase 4 rebuild).
   - Bot: `QuestModule(Base)`, `QuestInteractionModule`, renderers (`QuestEmbedRenderer`, `QuestComponentBuilder`),
     schedulers (`QuestBoardCleanupScheduler`, `QuestReminderScheduler`), notification/DM handlers, `OpModule`,
     `QuestAutocompleteProvider`.
   Many bot/web sites read settings via a service/bus already — confirm before touching.
6. Build infra + persistence + web + bot; run integration + persistence + bot tests to green.

## Phase 2 detail (gate)

- `PlatformFeature.Quests` in `Muster.Contracts/FeatureGating.cs`; `PlatformFeatureNames.Of` ⇒ `MusterQuests`;
  `GuildFeatureSource` case ⇒ `(await questSettings.GetAsync(guildId)).QuestsEnabled`.
- Enforce in `QuestAuthorizer` (block on `!CanEnable`), mirroring `ShopAuthorizer.ShopReachableAsync`. Guild-off
  stays the service's own result so codes/tests hold.
- Web: hide the Quests nav item in `GuildLayout` when not enabled; full-gate browse/detail/post/edit with
  `<FeatureGateNotice Feature="Quests" />`; soft-gate (`CanEnable`) the in-flight/claim/submission surfaces.
- Bot: gate via `MusterModuleBase` `feature: PlatformFeature.Quests` (+ `featureWindDown` for claim/submit actions)
  and inline `FeatureEnabledAsync` checks on interactive `/quest` commands. API: enforced via `QuestAuthorizer`.
- Add a `MusterQuests` flag to Web + Bot `appsettings.json` (`FeatureManagement` section), default true.

## Notes

- `QuestAuthorizer` already exists — it's the server-side gate chokepoint (like `ShopAuthorizer`).
- Browse (`Quests.razor`) is already query-param-driven with debounced search — Phase 5 is mostly restyle.
- Shop reference commit on this branch's base: `feat(shop)` (PR #11).
