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
- [x] Guild onboarding + rename/icon/owner + role snapshot sync: `GuildCreate`/`GuildUpdate` (`GuildLifecycleHandler`, `RoleLifecycleHandler`)
- [x] Guild teardown: `GuildDelete` marks the guild inactive
- [x] Member sync: lazy upsert on activity + `GuildUserAdd/Update/Remove` (`MemberLifecycleHandler`, `MemberSyncService`)
- [x] Authorization with lockout-proof bypass: owner **or** Discord Administrator/Manage-Guild **or** mapped admin/officer role (`GuildAuthorizationService`)
- [x] Participant gate: optional allowlist of Discord roles for who can earn/be tracked; empty = open to all (excludes guests when set)
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

- [x] Discord OAuth login + logout + cookie session; cascading auth state
- [x] Guild listing + access checks (reusing `GuildAuthorizationService`); SuperAdmin (host) still TODO
- [x] Guild dashboard + season leaderboard (`WebGuildService`, pinned web port for stable OAuth redirect)
- [x] Admin consoles: award, quest approval queue, season management, role-mapping config (gated by owner/admin/officer)
- [ ] Tracking-session management (incl. voice attendance view)
- [ ] Event-op management; muster management consoles
- [ ] Currency configuration; guild settings / reward config (web)
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
- [ ] Outbound "Coin" loot connectors (mint/spend via API + outbox)
- [ ] Rank thresholds → auto-assigned Discord roles
- [ ] Streaks / daily check-in
- [ ] Peer-to-peer kudos with budgets
- [ ] Anti-gaming controls (per-period caps, cooldowns)
- [ ] Privileged member-sync intent (full roster)
