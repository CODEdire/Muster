# Muster — Features & Implementation Checklist

Muster is a Discord participation-tracking bot with a Blazor SSR web UI. This file tracks
the road to **v1.0** and beyond. Check items off as they land. See [`docs/`](./docs) for
full design documentation.

**Stack:** .NET 10 · .NET Aspire · NetCord · EF Core / Azure SQL · Wolverine (CQRS +
durable outbox) · Blazor SSR · Azure Container Apps · Azure DevOps CI/CD.

---

## M0 — Base solution & scaffold

- [x] Solution layout (`src/`, `tests/`, `docs/`) with central package management
- [x] `Muster.Domain` — entities & enums (guilds, members, participation, scoring)
- [x] `Muster.Contracts` — broker-agnostic Wolverine message contracts
- [x] `Muster.ServiceDefaults` — Aspire OpenTelemetry / health / resilience
- [x] `Muster.Infrastructure` — EF Core `MusterDbContext` + initial migration
- [x] `Muster.Bot` — NetCord gateway worker (gateway wired; commands in M2)
- [x] `Muster.Web` — Blazor SSR shell + Discord OAuth + Wolverine.HTTP wiring
- [x] `Muster.MigrationService` — run-once EF migration job
- [x] `Muster.AppHost` — Aspire orchestration (Azure SQL, bot, web, migrations)
- [x] Unit + integration test projects
- [x] Planning docs in `docs/`
- [ ] CI build pipeline (build + test on PR)

## M1 — Data & persistence

- [x] Core entities and DbContext
- [x] Initial EF migration
- [x] Seed defaults on guild onboarding (POINTS currency, initial season) — `GuildProvisioningService`
- [x] Query services over the ledger and wallets — `ICurrencyReadService` (leaderboard + wallets)
- [ ] Integration tests against SQL via Testcontainers (incl. season leaderboard aggregation)

## M2 — Bot core

- [x] NetCord gateway intents (guilds, voice, reactions, scheduled events, messages, + privileged Server Members for member sync)
- [x] Guild onboarding + rename/icon/owner + role snapshot sync: `GuildCreate`/`GuildUpdate` (`GuildLifecycleHandler`, `RoleLifecycleHandler`)
- [x] Guild teardown: `GuildDelete` marks the guild inactive
- [x] Member sync: lazy upsert on activity + `GuildUserAdd/Update/Remove` (`MemberLifecycleHandler`, `MemberSyncService`)
- [x] Authorization with lockout-proof bypass: owner **or** Discord Administrator/Manage-Guild **or** mapped admin/officer role (`GuildAuthorizationService`)
- [x] Participant gate: optional allowlist of Discord roles for who can earn/be tracked; empty = open to all (excludes guests when set)
- [x] Quest Manager role (`QuestManagerRoleIds`) — create guild quests + arbitrate player bounties (`/config-questmanager-role`)
- [x] Player bounty board: post (escrow from own balance) → take → submit → owner confirm (payout) / cancel / dispute → Quest Manager arbitrate; expiry sweep. Atomic escrow + state changes (`BountyService`, `/bounty-*`, web bounty board)
- [x] Application-command framework wired (`AddApplicationCommands`)
- [x] Admin/officer gating of mutating commands via shared `MusterModuleBase`
- [x] `/config-admin-role`, `/config-officer-role`, `/config-show` (admin) — role mapping
- [ ] Command registration strategy (guild commands in dev, global in prod)
- [x] `/ping` command
- [ ] Bot install (OAuth2 invite) flow + documentation

## M3 — Participation methods

- [x] **Awarding engine** — idempotent ledger writes + wallet upsert (`AwardService`)
- [x] **Tracking sessions** — open/close + voice-minute rewards (`TrackingSessionService`)
- [x] **Voice attendance** capture from `VoiceStateUpdate` (`VoiceAttendanceHandler`)
- [x] **Reaction musters** — create + reward on reaction, capacity & idempotency (`MusterService` + `MusterReactionHandler`)
- [x] **Quests** — claim/submit/approve/reject, reward-on-approve (`QuestService`)
- [x] **Staff awards** — `/currency mint` (Discord) + web Award console, both via the audited `AdjustCurrency` CQRS command (`CurrencyLedgerSource.Adjustment`; actor in the audit trail). The standalone `/award` + `AwardCommandService` were retired into this path.
- [x] **Tracking sessions** — auto bind to Discord Scheduled Events (`ScheduledEventHandler`)
- [ ] **Reaction check-in** as a session attendance signal (distinct from musters)
- [x] **Event ops** — sign-up + attendance flow (`OpCommandService` + `/op-*`)
- [x] **Command-service abstraction** — Discord-independent `*CommandService` returning `CommandResult`; NetCord modules are thin adapters (directly unit-tested, no gateway needed)
- [x] **Slash-command modules** — `/currency` (balance/give/mint/adjust), `/leaderboard`, `/track-start`, `/track-stop`
- [x] **Slash-command modules** — `/muster` (REST post+react via `IMusterPublisher`) and `/quest-post|list|claim|submit|approve`
- [x] **Slash-command modules** — `/op-create|list|signup|close` (event ops)
- [x] Stats-only message activity + daily rollups + dedupe (`ActivityService` + `MessageActivityHandler`)
- [x] **Background tracking (P1)** — per-channel monitoring config (`TrackedChannel`) + always-on voice reward accrual with anti-AFK guards (unmuted/not-alone), daily cap, and Session-wins overlap (`BackgroundTrackingService`, `BackgroundFlushScheduler`, `/track-voice|track-text|track-untrack|track-channels`). See [SessionTracking.md](SessionTracking.md).
- [x] **Active-time stats + seasons + privacy (P2)** — unguarded active-time accrual → `DailyActivityRollup.VoiceMinutes` + per-season `SeasonParticipation` counter; message stats scoped to tracked text channels + `PointsPerMessage` reward; 4-state member tracking preference (`/track-privacy`) + guild `BackgroundTrackingOptIn` toggle (`/config-background-tracking`), enforced via `TrackingConsentResolver`.
- [x] **Session COIN minting (P3)** — sessions mint a guild-chosen spendable currency on close = `floor(minutes / MinutesPerCoin)` per attendee (`/config-session-coin`), alongside the POINTS award; `CurrencyLedgerSource.TrackingSession` with a `:coin` idempotency key.
- [x] **Participation reports + leaderboards (P4)** — `ParticipationReadService` (voice-time leaderboard season/all-time + per-member report of voice mins, messages, points by source); `/voice-leaderboard` command; admin CSV export `GET /guilds/{guildId}/participation/export.csv`.
- [x] **Guarded sessions (P5)** — session reward time runs on the snapshot/occupancy engine (`ReconcileSessionsAsync`); `GuildSettings.ApplyAfkGuardsToSessions` (default on) pauses muted/alone time; `VoiceAttendance.CarrySeconds` + startup void.
- [x] **Live ops + member self-view (P6)** — admin `Sessions.razor` (`/guilds/{id}/sessions`: active ops + voice leaderboard + CSV + history), nav-wired; voice panel on `MyProfile.razor`; read methods `ActiveSessionsAsync`/`RecentSessionsAsync`/`MemberVoiceStatsAsync` (live read isolated for a later SSE/SignalR push).
- [x] **Session UX + admin web polish (P6.1)** — named sessions + per-session anti-AFK guards (`/track-start name + skip-muted/skip-alone`, `skip-alone` off by default); `/track-stop` active-only autocomplete; all admin pages render in the guild shell; admin hub **Tracking** card; **Tracking settings** page (`/guilds/{id}/tracking`) for background opt-in, session guards, session-coin, and monitored channels.
- [x] **Sessions operational view (P6.2)** — Sessions on the nav rail (member-visible); tabbed SSR datagrid (Active + Leaderboard for members, History + CSV for staff) with search/sort/paging; drill-in `SessionDetail` roster (`/sessions/{id}`, any member); live opt-out CTA → web `TrackingChoice` control on `MyProfile`. Read layer: `PagedResult<T>`, paged active/recent queries, `SessionDetailAsync`.
- [x] **Hardening (P7)** — AllOut excluded from sessions + mid-session opt-out eviction + `MaxSessionHours` auto-close (P7a); configurable message anti-spam (`MessagesPerPoint`/cooldown/daily cap via `MessageRewardState`) (P7b); raw `ActivityRecord` pruning (`ActivityRetentionDays` + daily sweep) (P7c).
- [x] **Scale & robustness (P7.5)** — `GuildReconcileCoordinator` debounces voice-event bursts + serializes reconciles per guild (kills the thundering herd + bookkeeping races); 12h session flush clamp for gateway gaps. Config cache + leader-gating deliberately deferred (see SessionTracking.md).
- [x] **Min-segment threshold (P7d)** — `MinTrackedSeconds` drops drive-by session attendees (under the minimum) from the roster/award at leave + close.
- [x] **Sessions UX round A** — channels shown by name (stored + cache-refreshed); Background tab (who's tracked where, live); SessionDetail header + rules panel + member status (Active/Paused/Left via `VoiceAttendance.LastSeenAt`).
- [ ] Command registration strategy verified end-to-end (needs a live Discord app)

## M4 — Scoring, currency & messaging

- [x] Wolverine command/query bus in bot + web (`AddMusterMessaging`)
- [x] Wolverine EF Core + SQL Server durable outbox/inbox (wired; runtime-verified with SQL)
- [x] Broker-agnostic Wolverine contracts wired through thin handlers; every staged movement publishes `CurrencyMovementRecorded` (the single money-moved seam)
- [x] Multi-currency ledger (seasonal POINTS + persistent spendable currencies)
- [x] Seasons — `/season-start|end|status`, archive on rollover (`SeasonService`)
- [x] Wallets / balance projection + leaderboards (`ICurrencyReadService`)
- [x] Per-guild reward configuration (`GuildSettings.PointsPerVoiceMinute`)
- [~] Sagas / scheduled messages — event-driven session lifecycle done (scheduled-event bind, reaction/voice); time-based reminders & season-end auto-archive deferred until the bus runs against SQL (needs live verification)

## M5 — Web UI & API

- [x] Discord OAuth login + logout + cookie session; cascading auth state
- [x] Guild listing + access checks (reusing `GuildAuthorizationService`); SuperAdmin (host) still TODO
- [x] Guild dashboard + season leaderboard (`WebGuildService`, pinned web port for stable OAuth redirect)
- [x] Admin consoles: award, quest approval queue, season management, role-mapping config (gated by owner/admin/officer)
- [ ] Tracking-session management (incl. voice attendance view)
- [ ] Event-op management; muster management consoles
- [x] Currency configuration (web): create/edit currencies — code, name, seasonal, spendable, mode
- [ ] Guild settings / reward config (web)
- [x] Audit log: recorded for admin actions (bot + web) + searchable/filterable/sortable console with CSV export
- [x] Member self view (`/me`) + admin member detail (wallets + ledger history)
- [x] Error + 404 pages; expired-session redirect to login; friendly 403 with correct status
- [x] Public API (`/api/v1`) — Wolverine.HTTP endpoints (leaderboard, wallets, ledger) + API-key auth
- [x] API client management (web) + guarded currency mint/spend with overdraft protection
- [x] Currency modes (`Internal`/`External`/`Hybrid`) modeled for connector authority

## M6 — Deployment & CI/CD

- [ ] `azd infra synth` Bicep from AppHost
- [ ] Azure resources: Container Apps env, Azure SQL, Key Vault, ACR, Log Analytics/App Insights
- [ ] Managed identity + passwordless SQL (Entra); ACR pull identity
- [ ] Azure DevOps pipeline: restore → build → test → `azd` deploy + migration job
- [ ] Bot ACA singleton (min=max=1); web ACA scales 1..N
- [ ] dev → staging → prod environments

## M7 — Hardening & launch

- [ ] Observability dashboards + alerting (App Insights)
- [ ] Health checks + graceful gateway shutdown / RESUME handling
- [ ] Privacy Policy + Terms of Service pages; data retention & deletion
- [ ] Secrets rotation runbook
- [ ] Test coverage for scoring/ledger; load sanity check
- [ ] Documentation complete; v1.0 production cutover

## Post-v1 (backlog)

- [ ] Enable Azure Service Bus transport (decoupled bot ↔ web messaging)
- [ ] Discord gateway sharding (scale beyond ~2500 guilds)
- [x] Outbound currency connectors — configurable per-currency HTTP economy API (auth/signing/templated Credit/Debit/GetBalance, response parsing, encrypted secrets), called **synchronously before commit** so a failed external push aborts the operation (`CurrencyService.StageAsync`); idempotent inbound mirror mint/spend (`externalId`), loop-guarded; admin UI + test-send + wallet rebuild + balance sync sweep (see `Currency.md`). *(Discord message-command transport + member shop are wishlist.)*
- [x] **Currency CQRS funnel** — `TransferCurrency` (member→member) + `AdjustCurrency` (staff mint/correct) as authorized (`ICurrencyAuthorizer`) + audited `IGuildCommand`s; `/currency` Discord tree (`balance`/`give`/`mint`/`adjust`) + API (`transfer`/`adjust`/list/balance). One `ICurrencyService` owns transaction + ledger + external call.
- [x] **Hybrid balance + history pruning** — ledger `SUM` is the transaction authority (overdraft); `Wallet.Balance` is a cheap display cache (dashboard/leaderboard). `LedgerPruneService` folds history beyond the retention window into per-scope `Checkpoint` carry-forward rows (balances preserved, SUM bounded) — daily via `LedgerPruneScheduler` and on **season archive** (`SeasonService` → `CheckpointSeasonAsync`). Retention is per-guild (`LedgerRetentionDays`) combined with a platform cap/default (`Currency:MaxLedgerRetentionDays` in AppSettings); set + validated in web `ConfigAdmin` and `/config-ledger-retention`. Entities reorganized one-per-aggregate under `Muster.Domain.Entities.<Feature>`.
- [ ] Rank thresholds → auto-assigned Discord roles
- [ ] Streaks / daily check-in
- [ ] Peer-to-peer kudos with budgets
- [ ] Anti-gaming controls (per-period caps, cooldowns)
- [ ] Privileged member-sync intent (full roster)

## Quest system — remaining work

The quest engine is complete (lifecycle, intake/final approval, auto-resolve, tiers/points, capacity,
revisions, edit, optimistic concurrency, audits, player limits) with event/notification seams in place.
What's left, to revisit:

### Wiring the seams (highest value)
- [ ] Discord notification delivery — add a Wolverine consumer for `QuestLifecycleNotified` that DMs the
      right person / posts per lifecycle moment (QuestService already publishes; nothing consumes yet)
- [ ] Formatted quest board post — post/edit/close the Discord message on create + state changes
      (the `Created` event is reserved for this; `GuildEvent.ChannelId`/`MessageId` hold the event's announce message)
- [ ] External reward connector — implement `IQuestRewardSink` so the CurrencyService / loot system
      resolves rewards on `QuestCompletion` (currently a logging stub)

### Deferred features
- [ ] Personal-quest multi-taker — needs N× escrow and per-participant settlement/dispute/final state
      (personal quests are single-taker for now; guild quests already support capacity)
- [ ] Editing a personal quest's reward (escrowed — cancel/repost only today)

### Polish / consistency
- [ ] Apply the gateway-handler scope-safety helper to the other singleton handlers
      (`MemberLifecycleHandler`, `MessageActivityHandler`, `MusterReactionHandler`, `RoleLifecycleHandler`,
      `ScheduledEventHandler`, `VoiceAttendanceHandler`)
- [ ] Clear "out of revisions" message on the personal revision-cap path (guild path already has one)

### Verification
- [ ] Run `BackfillGuildSettings` migration on existing databases before using the settings pages
- [ ] Live run-through of the web board flows and slash commands (only unit tests + boot so far)
