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
- [x] Query services over the ledger and wallets — `ScoreQueryService` (leaderboard + wallets)
- [ ] Integration tests against SQL via Testcontainers (incl. season leaderboard aggregation)

## M2 — Bot core

- [x] NetCord gateway intents (guilds, voice, reactions, scheduled events, messages, + privileged Server Members for member sync)
- [x] Guild onboarding + rename/icon sync: `GuildCreate`/`GuildUpdate` via `GuildLifecycleHandler`
- [ ] Guild teardown: `GuildDelete` marks the guild inactive
- [x] Member sync: lazy upsert on activity + `GuildUserAdd/Update/Remove` (`MemberLifecycleHandler`, `MemberSyncService`)
- [x] Role-mapping authorization: admin/officer derived from Discord role ids in `GuildSettings` (`GuildAuthorizationService`)
- [x] Application-command framework wired (`AddApplicationCommands`)
- [ ] Command registration strategy (guild commands in dev, global in prod)
- [x] `/ping` command
- [ ] `/config` (admin) command
- [ ] Bot install (OAuth2 invite) flow + documentation

## M3 — Participation methods

- [x] **Awarding engine** — idempotent ledger writes + wallet upsert (`AwardService`)
- [x] **Tracking sessions** — open/close + voice-minute rewards (`TrackingSessionService`)
- [x] **Voice attendance** capture from `VoiceStateUpdate` (`VoiceAttendanceHandler`)
- [x] **Reaction musters** — create + reward on reaction, capacity & idempotency (`MusterService` + `MusterReactionHandler`)
- [x] **Quests** — claim/submit/approve/reject, reward-on-approve (`MissionService`)
- [x] **Manual / bulk awards** — `ManualAwardService`
- [x] **Tracking sessions** — auto bind to Discord Scheduled Events (`ScheduledEventHandler`)
- [ ] **Reaction check-in** as a session attendance signal (distinct from musters)
- [x] **Event ops** — sign-up + attendance flow (`OpCommandService` + `/op-*`)
- [x] **Command-service abstraction** — Discord-independent `*CommandService` returning `CommandResult`; NetCord modules are thin adapters (directly unit-tested, no gateway needed)
- [x] **Slash-command modules** — `/award`, `/leaderboard`, `/wallet`, `/track-start`, `/track-stop`
- [x] **Slash-command modules** — `/muster` (REST post+react via `IMusterPublisher`) and `/quest-post|list|claim|submit|approve`
- [x] **Slash-command modules** — `/op-create|list|signup|close` (event ops)
- [x] Stats-only message activity + daily rollups + dedupe (`ActivityService` + `MessageActivityHandler`)
- [ ] Command registration strategy verified end-to-end (needs a live Discord app)

## M4 — Scoring, currency & messaging

- [x] Wolverine command/query bus in bot + web (`AddMusterMessaging`)
- [x] Wolverine EF Core + SQL Server durable outbox/inbox (wired; runtime-verified with SQL)
- [x] Broker-agnostic contracts wired through handlers (`AwardCurrency`, `AdjustCurrencyBalance`, `MemberParticipated` → cascade `LedgerEntryRecorded`)
- [x] Multi-currency ledger (seasonal POINTS + persistent spendable currencies)
- [x] Seasons — `/season-start|end|status`, archive on rollover (`SeasonService`)
- [x] Wallets / balance projection + leaderboards (`ScoreQueryService`)
- [x] Per-guild reward configuration (`GuildSettings.PointsPerVoiceMinute`)
- [~] Sagas / scheduled messages — event-driven session lifecycle done (scheduled-event bind, reaction/voice); time-based reminders & season-end auto-archive deferred until the bus runs against SQL (needs live verification)

## M5 — Web UI & API

- [ ] Discord OAuth login + cookie session
- [ ] Authorization: SuperAdmin / GuildAdmin / Member
- [ ] Guild dashboard + season leaderboards
- [ ] Tracking-session management (incl. voice attendance view)
- [ ] Quest board + approval queue; event-op management
- [ ] Muster management; manual/bulk award console
- [ ] Season & currency configuration; guild settings / reward config
- [ ] Member detail with wallets; audit log
- [ ] Wolverine.HTTP public API (`/api/v1`) — read endpoints + API-key auth
- [ ] API client management; currency mint/spend endpoints (guarded)

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
- [ ] Outbound "Coin" loot connectors (mint/spend via API + outbox)
- [ ] Rank thresholds → auto-assigned Discord roles
- [ ] Streaks / daily check-in
- [ ] Peer-to-peer kudos with budgets
- [ ] Anti-gaming controls (per-period caps, cooldowns)
- [ ] Privileged member-sync intent (full roster)
