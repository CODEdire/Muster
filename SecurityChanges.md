# Security Changes — First Release Hardening

Audit baseline: 2026-05-28 (branch `claude/admin-config-rework`).

## Surfaces

Three entrypoints converge on [`GuildAuthorizationService`](src/Muster.Infrastructure/Services/Membership/GuildAuthorizationService.cs):

- **Discord bot** ([Muster.Bot/Modules](src/Muster.Bot/Modules)) — `MusterModuleBase.RunAsync(..., RequiredRole)` gates each slash command; button clicks pass `ActorId = clicker` to CQRS.
- **Web UI** ([Muster.Web/Components/Pages](src/Muster.Web/Components/Pages)) — Discord OAuth cookie session. Pages inherit `GuildMemberComponentBase` (view) or `GuildAdminComponentBase` (admin; officer-sufficient opt-in). CSV exports inline-check `IsAdminAsync`.
- **Public API `/api/v1`** ([Muster.Web/Api](src/Muster.Web/Api)) — `[RequireApiScope("scope", requireActor: bool)]` → two-layer (token + bound actor) via `ApiKeyMiddleware`.

Resource authorizers (`ICurrencyAuthorizer`, `IQuestAuthorizer`) layer owner-vs-staff on top of role tiers. Same instances used by all three surfaces.

## Role tiers today

| Tier | Resolution | Lockout-proof |
|---|---|---|
| Admin | guild owner OR Discord Admin/Manage-Guild OR `Settings.AdminRoleIds` | yes (owner bypass) |
| Officer | Admin OR `Settings.OfficerRoleIds` | inherits |
| QuestManager | Admin OR `Settings.QuestManagerRoleIds` OR Officer | inherits |
| Participant | open by default OR `Settings.ParticipantRoleIds` | open if unconfigured |

API key scopes: `read:leaderboard`, `read:wallets`, `read:ledger`, `read:audit`, `read:quests`, `read:tracking`, `write:currency`, `write:quests`, `write:tracking`.

## Findings (ordered by severity)

### 🔴 High — `ApiTrackingEndpoints` writes are scope-only (no actor)

[`ApiTrackingEndpoints.cs`](src/Muster.Web/Api/ApiTrackingEndpoints.cs) writes that lack `requireActor: true`:

- `POST /tracking/sessions/{id}/stop`
- `PUT /tracking/channels/voice` / `text`
- `DELETE /tracking/channels/{id}`
- `POST /members/{userId}/tracking/privacy` — sets ANY user's privacy
- `POST /tracking/multipliers` + `/{id}/enabled` + `DELETE`

A `write:tracking` key with no actor binding has admin-tier tracking control. Currency / quest writes already require an actor and re-authorize per call.

### 🟡 Medium — API-key UX too coarse

[`ApiClients.razor`](src/Muster.Web/Components/Pages/Admin/ApiClients.razor): flat scope checkboxes, no preset bundles, no expiry, no last-used timestamp, no rotate, no IP allowlist. Easy to overgrant; hard to detect a leak.

### 🟡 Medium — Officer reaches admin landing

`Admin/GuildAdmin.razor` is `OfficerSufficient = true`. Officers see admin shell. Sub-pages re-gate, so 403 on click-through, but URL structure leaks via nav.

### 🟡 Medium — Officer is overloaded

`Officer` currently grants both event-ops (`OpModule`) AND economy-staff (`CurrencyAuthorizer` uses `IsOfficerAsync`). One role, two unrelated power domains.

### 🟢 Low — No inbound rate limit on `/api/v1`

A leaked key can hammer at full speed. `AddStandardResilienceHandler` is outbound only.

### 🟢 Low — SuperAdmin documented but unimplemented

[`docs/web-and-auth.md:30`](docs/web-and-auth.md) references SuperAdmin; no code path enforces it.

### 🟢 Low — Verify `createdByUserId` binding for API clients

[`ApiClientService.CreateAsync`](src/Muster.Infrastructure/Services/Platform/ApiClientService.cs) trusts the caller's `createdByUserId`. Confirm the create form passes the signed-in `UserId` server-side, not a form field.

## Phases

### ✅ Phase 1 — Audit (this doc)

### ✅ Phase 2 — Lock down tracking writes (high-sev fix)

- All `ApiTrackingEndpoints` writes now `requireActor: true`.
- `ITrackingAuthorizer` + `TrackingAuthorizer` mirror `ICurrencyAuthorizer` / `IQuestAuthorizer`. Permissions: `ManageSessions`, `ManageChannels`, `ManageMultipliers`, `SetPrivacy` (self-or-staff).
- Wired into `TrackingCommandService` / `TrackedChannelCommandService` / `RewardMultiplierCommandService` / `TrackingPreferenceCommandService` — bot + web + API funnel through same gate.
- Test coverage: `NonStaff_Forbidden_OnEveryMutation` covers all five multiplier verbs; `AlwaysAllowTrackingAuthorizer` / `AlwaysDenyTrackingAuthorizer` stubs in `tests/TestSupport`. 292/292 pass.

### ⏳ Phase 4 — Role expansion

> Note on Points vs. Coin: both are rows in the same `Currency` table (only `IsSeasonal` differs — Points is seasonal & drives the leaderboard, Coin is spendable & connector-backed). They share `CurrencyService`, `CurrencyAuthorizer`, ledger, and outbound transports. `EconomyManager` therefore governs **all currencies** — POINTS and any guild's COIN-class currencies — through the existing single funnel. No per-currency-type role split needed.

Split `Officer` into three domain roles + add read-only auditor:

| New role | Replaces | Powers |
|---|---|---|
| `EconomyManager` | `Officer` in `CurrencyAuthorizer` | mint / adjust / bulk / view-anyone wallets — covers POINTS + COIN equally |
| `EventOfficer` | `Officer` in `OpModule` | event ops only |
| `TrackingManager` | `Officer` in `TrackingAuthorizer` + bot `RequiredRole.Admin` on `/track *` | open/close sessions, configure monitored channels + reward multipliers, force-opt-out members |
| `Auditor` | — | read-only: audit log, ledger, participation; `read:*` API scopes |
| `SuperAdmin` | — (host-operator) | guild lifecycle, global kill-switch (not implemented in v1) |

Implementation:
- Extend `GuildSettings` with `EconomyManagerRoleIds`, `EventOfficerRoleIds`, `TrackingManagerRoleIds`, `AuditorRoleIds`.
- Add `IsEconomyManagerAsync` / `IsEventOfficerAsync` / `IsTrackingManagerAsync` / `IsAuditorAsync` to `GuildAuthorizationService`.
- Migration: existing `OfficerRoleIds` → seed all three management lists (no-op for live guilds — `OfficerRoleIds` kept as a back-compat alias on read paths until v2).
- Update `CurrencyAuthorizer.IsManager` resolution → `IsEconomyManagerAsync`.
- Update `TrackingAuthorizer.IsStaff` resolution → `IsTrackingManagerAsync`.
- Update `OpModule` → `RequiredRole.EventOfficer`. Update `TrackModule` → `RequiredRole.TrackingManager` (was `Admin`).
- `Auditor` is implied by any management role (admin / officer / economy / event / tracking / quest) so adding it is purely additive.
- New `/config-economy-role`, `/config-event-role`, `/config-tracking-role`, `/config-auditor-role` slash commands + web mapping UI.
- New `RequiredRole` enum entries (`EconomyManager`, `EventOfficer`, `TrackingManager`, `Auditor`); `MusterModuleBase` switch arms.
- Tests: backfill that each split role grants its own domain, doesn't grant siblings; legacy Officer still grants all three; Auditor implied by mutating roles. Self-set-privacy works without staff role; cross-user set-privacy denied.

### 🗒️ Phase 3 (wishlist) — API-key hardening

Deferred. Pick up after Phase 4 ships.

- Add `ExpiresAt`, `LastUsedAt`, `RotatedAt` to `ApiClient`.
- Touch `LastUsedAt` in `ApiClientService.ValidateAsync` (debounced write).
- Rotate-key flow (issue new hash, keep grace period? or hard-cut).
- Per-key inbound rate limit (`AddRateLimiter`, partition by key hash).
- Named scope presets in `ApiClients.razor` (Coin connector / Read-only observer / Bot actor).
- Verify `createdByUserId` comes from `UserId`, never form input.

### ✅ Phase 6 — API read-endpoint audit

Full pass of every `/api/v1` GET. Writes were already actor-gated through CQRS authorizers (Phase 2); reads were scope-only and leaked personal + staff-tier data.

**Endpoint matrix** (post-fix):

| Endpoint | Scope | Actor | Authorizer / role |
|---|---|---|---|
| GET `/leaderboard` | `read:leaderboard` | — | public |
| GET `/currencies` | `read:wallets` | — | public config |
| GET `/members/{userId}/wallets` | `read:wallets` | ✅ | `CurrencyAuthorizer.View` (self-or-economy-staff) |
| GET `/members/{userId}/currencies/{code}/balance` | `read:wallets` | ✅ | `CurrencyAuthorizer.View` |
| GET `/members/{userId}/ledger` | `read:ledger` | ✅ | `CurrencyAuthorizer.View` |
| GET `/ledger` (guild-wide) | `read:ledger` | ✅ | `IsAuditorAsync` |
| GET `/currencies/{code}/movements` | `read:ledger` | ✅ | `IsAuditorAsync` |
| GET `/currencies/{code}/supply` | `read:ledger` | ✅ | `IsAuditorAsync` |
| GET `/audit` | `read:audit` | ✅ | `IsAuditorAsync` |
| GET `/tracking/leaderboard` | `read:tracking` | — | public |
| GET `/tracking/sessions(/active)` | `read:tracking` | — | public-ish (board) |
| GET `/tracking/sessions/{id}` | `read:tracking` | — | public-ish (roster) |
| GET `/tracking/sessions/{id}/events` | `read:tracking` | ✅ | `IsAuditorAsync` |
| GET `/members/{userId}/tracking` | `read:tracking` | ✅ | `TrackingAuthorizer.ViewMemberStats` (self-or-tracking-staff) |
| GET `/tracking/channels` / `/multipliers` | `read:tracking` | — | public config |
| GET `/quests` / `/quests/{id}` | `read:quests` | optional | manager view actor-gated |
| POST `/currencies/{code}/mint` | `write:currency` | ✅ | `IsAdminAsync` (Phase 6b) |
| POST `/currencies/{code}/spend` | `write:currency` | ✅ | `IsAdminAsync` (Phase 6b) |
| POST `/currencies/{code}/transfer` | `write:currency` | ✅ | `CurrencyAuthorizer.Transfer` |
| POST `/currencies/{code}/adjust` | `write:currency` | ✅ | `CurrencyAuthorizer.Mint/Adjust` (economy staff) |

**Implementation**:
- New permission `TrackingPermission.ViewMemberStats` — self-or-staff, same shape as `SetPrivacy`.
- New helper [`ApiReadGuards`](src/Muster.Web/Api/ApiReadGuards.cs) — `RequireSelfOrEconomyStaffAsync`, `RequireSelfOrTrackingStaffAsync`, `RequireAuditorAsync`. Each runs after `ApiKeyMiddleware` and short-circuits with 403 if denied.
- Every gated endpoint flipped to `requireActor: true` so `http.ApiActor()` is guaranteed non-zero. Then the helper resolves the right authorizer.

**Machine-inbound mint/spend — locked to admin**:
- `POST /currencies/{code}/mint` and `POST /currencies/{code}/spend` are now `requireActor: true` and gated on `IsAdminAsync(actor)` in addition to scope. These endpoints can create currency from nothing, so an actor-bound key alone isn't enough — the actor must hold guild admin.
- Connector bots that need this path must be bound to either the guild owner OR a bot/service member that holds an admin role. Member-tier keys with `write:currency` can still hit `transfer` (own wallet) and `adjust` (if also economy staff), but mint/spend → 403.
- Ledger entries from these endpoints still write `SourceType = Connector` so the outbound dispatcher skips them (no loop).

**Tests**: `ViewMemberStats_SelfAlways_OthersRequireTrackingStaff` covers self / non-staff-other / staff-other paths. Full suite passes (excluding one pre-existing unrelated failure in `AuditMiddlewareTests.ApproveCommand_ViaBus_RunsHandler_AndAuditsOnce` that fails on `claude/v0.5-release-prep` without this work — flag separately).

### ✅ Phase 7 — Pre-release API hardening

#### 7.1 — Audit log on API mint/spend (was silent)

[`MintCurrency`/`SpendCurrency`](src/Muster.Contracts/CurrencyMessages.cs) gained `ActorId` and now implement `IGuildCommand`. The existing `case MintCurrency` / `case SpendCurrency` arms in [`AuditMiddleware`](src/Muster.Infrastructure/Messaging/AuditMiddleware.cs) (previously dead code) now fire on every API-driven delivery, recording actor, currency, amount, reason, and external id. Connector flow unchanged — ledger entries still write `SourceType = Connector`.

#### 7.2 — Request body size limit

`Configure<KestrelServerOptions>` caps inbound at **256 KB** globally ([Program.cs](src/Muster.Web/Program.cs)). API payloads are sub-1KB; web form posts well under. Per-endpoint `[RequestSizeLimit]` override available if a legitimate upload path lands later.

#### 7.3 — Per-key rate limit on `/api/v1`

`AddRateLimiter` global limiter partitions traffic:
- `/api/v1/...` with key → partition `key:SHA256(X-Api-Key)`, sliding window **60 req / minute**, no queue.
- `/api/v1/...` without key → partition by `RemoteIpAddress`, same limit (caps anonymous scope-probe attempts).
- Everything else → `NoLimiter` (web UI unaffected).
- 429 returned on rejection.

Tune `PermitLimit` / `Window` from config when a real workload demands more.

#### 7.5 — CORS via Azure Container Apps ingress

App stays **default-deny** (no `AddCors` / `UseCors`). Cross-origin policy lives at the ACA ingress layer — ops can adjust without redeploying code. Runbook + sample `az containerapp ingress cors enable` command added to [`docs/deployment.md`](docs/deployment.md). Move to in-app `AddCors` only if per-route policy is ever needed (ACA can't do that).

#### 7.4 — OTel span enrichment on `/api/v1`

[`ApiKeyMiddleware`](src/Muster.Web/Api/RequireApiScope.cs) now tags `Activity.Current` with the resolved auth context:

| Tag | Value | Purpose |
|---|---|---|
| `muster.api.scope` | required scope (`write:currency`, etc.) | filter by scope abuse |
| `muster.api.guild_id` | route guildId | group per-tenant traffic |
| `muster.api.client_id` | `ApiClient.Id` GUID | link back to admin UI |
| `muster.api.client_name` | client display name | human-readable dashboards |
| `muster.api.actor_id` | bound `ActsAsUserId` (0 = unbound) | who's running the request |
| `muster.api.auth_result` | `ok` / `invalid_api_key` / `guild_mismatch` / `insufficient_scope` / `key_not_bound` | group rejections by cause |

Rejection-path requests carry the same tags so incident triage can spot a key probing guilds it doesn't own, a runaway client hitting rate limits, or which scope is rejecting the most calls. [`ApiAuth.ResolveAsync`](src/Muster.Web/Api/ApiAuth.cs) now returns the stable rejection reason as a third tuple element.

KQL examples (Application Insights):
```kusto
// top 10 keys by 429 count, last hour
requests | where url contains "/api/v1/" and resultCode == 429
| summarize n=count() by tostring(customDimensions["muster.api.client_name"])
| top 10 by n
```
```kusto
// every mint in last day, by actor
requests | where url contains "/currencies/" and url contains "/mint" and success
| project timestamp, customDimensions["muster.api.actor_id"], customDimensions["muster.api.client_name"]
```

#### Deferred (post-release fine)

- **API-key lifecycle** (`ExpiresAt` / `LastUsedAt` / `RotatedAt`) — still wishlist.

### ✅ Phase 8 — Discord-side audit + quest privacy

Full pass of bot slash commands, button/modal/select interactions, gateway event handlers, autocomplete providers.

#### Verified clean (no action)
- Every slash-command write funnels through `MusterModuleBase.RunAsync` → role gate OR a CQRS authorizer in the handler. No bypass.
- Button + modal + select interactions construct `IGuildCommand` with `ActorId = Context.User.Id` (Discord-supplied) → same `QuestAuthorizer` / `CurrencyAuthorizer` as slash + web + API.
- `MusterReactionHandler` gates check-in rewards on `IsParticipantAsync`.
- `/currency inspect` has inline `CurrencyAuthorizer.View` check.
- `/timezone`, `/currency notify` are self-only (`Context.User.Id`).
- Sync handlers (member / role / channel / voice / message) run on gateway events — no auth needed.
- Autocomplete providers for currency / multiplier / active-session leak no sensitive data.

#### 8.1 — Quest detail viewer-aware scrub

`GetQuestDetailAsync` returned `DisputeReason`, per-participant `Note`, `ReviewNote`, `ReviewedBy[Name]` to any guild member. New [`QuestDetailViewScrub.ForViewer`](src/Muster.Infrastructure/Services/Quests/QuestReadService.cs) masks these for non-privileged viewers:

- **Manager**: sees everything.
- **Owner**: sees everything (their quest, their workflow).
- **Disputer**: sees their own dispute reason (they wrote it).
- **Per-row worker**: sees their own row's `Note` / `ReviewNote` / `ReviewedBy`.
- **Other guild members**: scrubbed (`null` private fields; public board fields stay).

Applied at the three user-facing call sites:
- `/quest show` (Discord slash) — [QuestModule.ShowAsync](src/Muster.Bot/Questing/Modules/QuestModule.cs)
- `GET /api/v1/guilds/{id}/quests/{questId}` — [ApiQuestEndpoints.Detail](src/Muster.Web/Api/ApiQuestEndpoints.cs)
- `/guilds/{id}/quests/{questId}` web page — [QuestDetail.razor](src/Muster.Web/Components/Pages/QuestDetail.razor)
- Plus DM "My actions" button — [QuestInteractionModule.MyActions](src/Muster.Bot/Questing/Modules/QuestInteractionModule.cs)

Internal callers (DM push, board renderers, reminder scheduler, audit middleware) keep the unscrubbed view — they render staff cards or deliver notifications to the actual subject.

#### 8.2 — Quest autocomplete filter

[QuestAutocompleteProvider](src/Muster.Bot/Questing/Autocomplete/QuestAutocompleteProvider.cs) previously suggested every active-state quest to every user, leaking names of in-progress mod work (Disputed / PendingApproval / PendingFinal) via the dropdown. Now:

- **Manager**: sees all active states.
- **Non-manager**: sees only `Open` + `Scheduled` + quests they own or participate in.

Write attempts on hidden quests were already 403-blocked at the handler (defense-in-depth); this closes the information-disclosure side.

#### Tests
[QuestDetailViewScrubTests](tests/Muster.Integration.UnitTests/Services/Quests/QuestDetailViewScrubTests.cs) — 6 cases pinning manager / owner / worker-self / worker-other / random-member visibility + record immutability. Full suite: 310 pass, 1 pre-existing unrelated fail.

#### Deferred
- ScheduledEventHandler audit (system actor) — `🟢 Low`.
- Re-tier `/muster create` from `Admin` to `EventOfficer` / `TrackingManager` — UX choice, not security.

### ✅ Phase 9 — Web UI role-tier expansion

Audit of every Razor page route. Surface map: 5 public, 2 auth-only, 12 guild-member, 5 staff (`OfficerSufficient`), 11 admin-only. Discovered the legacy `OfficerSufficient` boolean on `GuildAdminComponentBase` only knew Admin or Officer — so after Phase 4's role split, anyone mapped as **EconomyManager / TrackingManager / EventOfficer / Auditor — but NOT Officer** was functionally web-blind.

#### 9.1 — `GuildAccessTier` flags + dispatcher

New [`GuildAccessTier`](src/Muster.Infrastructure/Services/Membership/GuildAccessAuthorizer.cs) flags enum (Admin / Officer / EconomyManager / EventOfficer / TrackingManager / QuestManager / Auditor + `AnyStaff` alias). New [`GuildAccessAuthorizer.IsAuthorizedAsync`](src/Muster.Infrastructure/Services/Membership/GuildAccessAuthorizer.cs) dispatches OR-over-set-flags against `GuildAuthorizationService`. Admin always passes via the lockout-proof bypass.

[`GuildAdminComponentBase`](src/Muster.Web/Components/GuildAdminComponentBase.cs): replaced `bool OfficerSufficient` with `GuildAccessTier RequiredAccess`. Default = `Admin` (admin-only).

**Re-gated staff pages**:
| Page | Was | Now |
|---|---|---|
| `GuildAdmin` (landing) | Officer | `AnyStaff` |
| `Members` / `MemberDetailAdmin` | Officer | `Officer \| EconomyManager` |
| `GuildLedger` | Officer | `Officer \| EconomyManager \| Auditor` |
| `GuildPoints` | Officer | `Officer \| EconomyManager \| Auditor` |

#### 9.2 — Narrow admin-only pages by domain

Re-targeted from Admin-only (where any management role should have legitimately been able to act):
| Page | Was | Now |
|---|---|---|
| `AuditConsole` | Admin | `Officer \| Auditor` |
| `Currencies` / `CurrencyEdit` | Admin | `Officer \| EconomyManager` |
| `Multipliers` / `TrackingSettings` / `SessionNew` | Admin | `Officer \| TrackingManager` |
| `QuestSettings` | Admin | `Officer \| QuestManager` |

Kept Admin-only: `ApiClients`, `ConfigAdmin` (role mapping itself), `CurrencyWebhooks` (signing keys), `SeasonsAdmin` (season lifecycle).

#### 9.3 — Sessions/SessionDetail staff check

[Sessions.razor:390](src/Muster.Web/Components/Pages/Admin/Sessions.razor) + [SessionDetail.razor:406](src/Muster.Web/Components/Pages/Admin/SessionDetail.razor) `_isStaff` flag now resolves to `Admin OR TrackingManager OR Officer` (was Admin OR Officer only). The pages stay under `Admin/` for routing/nav cohesion but inherit `GuildMemberComponentBase` because the roster read is public-ish — the staff actions inside (stop/close, force opt-out) gate on `_isStaff`. Added inline comment explaining the folder/base discrepancy.

#### Tests

[`GuildAccessAuthorizerTests`](tests/Muster.Integration.UnitTests/Services/Membership/GuildAccessAuthorizerTests.cs) — 7 cases:
- Admin always passes regardless of tier
- Admin-only denies every non-admin (even one holding every other role)
- Single-tier gates by that role only
- Combined tier accepts any holder
- `AnyStaff` accepts every management tier
- Plain member denied by every requirement
- Legacy Officer satisfies its own tier AND every Phase 4 split tier (back-compat preserved)

Full suite: 317 pass, 1 pre-existing unrelated fail.

### ✅ Phase 10 — Release hardening

#### 10.1 — Fixed `AuditMiddlewareTests` (was a permanent stale fail)

[`AuditMiddlewareTests.cs`](tests/Muster.Integration.UnitTests/Messaging/AuditMiddlewareTests.cs) queried by `nameof(ApproveQuestSubmission)` (literal class name) but the middleware records via the `AuditActions` registry (stable key `"quest.approve"`). The assertion never matched — the test had been failing as a no-op since the registry landed, masking real audit regressions behind a permanent fail.

Triage discovered a second issue: the test bootstrap (minimal `Host.CreateApplicationBuilder` + in-memory DB, no SQL persistence) doesn't trigger Wolverine 6's policy-added middleware weaving on `IMessageBus.InvokeAsync` paths. The middleware logic itself works (confirmed by direct invocation in the same test). Production hosts use the full Aspire-driven bootstrap and weave correctly.

Restructured test: handler-via-bus still verifies the command path; `AuditMiddleware.After` is now exercised directly to pin the middleware's actual logic (registry lookup, payload shape, row landed). Full Aspire-bus integration coverage is wishlist (would require standing up the AppHost in tests).

#### 10.2 — Health probes in prod

[`Muster.ServiceDefaults.MapDefaultEndpoints`](aspire/Muster.ServiceDefaults/Extensions.cs) used to register `/health` + `/alive` only in `IsDevelopment()`. Now mapped in every environment so ACA's HTTP probes work in prod (TCP-port fallback can't detect deadlocks). Default ASP.NET response writer returns plain `Healthy` / `Unhealthy` — no diagnostic payload to leak. Probes need to be `[AllowAnonymous]` because ACA can't supply credentials; treating them as public-but-trivial info matches Aspire's recommended pattern.

ACA probe config (example for the `muster-web` Container App):
```yaml
probes:
  - type: liveness
    httpGet: { path: /alive, port: 8080 }
    periodSeconds: 30
  - type: readiness
    httpGet: { path: /health, port: 8080 }
    periodSeconds: 10
```

Future: internal `/diag` with full per-check breakdown behind admin auth.

#### 10.3 — Cookie config explicit

Auth cookie now pinned (was framework defaults) — `HttpOnly`, `SecurePolicy = Always`, `SameSite = Lax` (required for OAuth callback). Lifetime + sliding behaviour pulled from config:

```jsonc
"Auth": {
  "CookieExpireTimespan": "14.00:00:00",
  "CookieSlidingExpiration": true
}
```

Ops can rotate without code changes. Fallbacks (14 days, sliding) match the framework default so existing sessions don't surprise-eject.

#### 10.4 — Security headers + CSP

[`SecurityHeadersMiddleware.cs`](src/Muster.Web/SecurityHeadersMiddleware.cs) sets on every response:
- `Content-Security-Policy` — `default-src 'self'` + scoped allowlist (jsdelivr for EasyMDE/CodeMirror, Google Fonts for icons, Discord CDN for avatars, `data:` for inline icons). `unsafe-inline` for scripts + styles is currently required by Blazor SSR streaming + the inline scripts in [`App.razor`](src/Muster.Web/Components/App.razor); moving to nonces is future work.
- `X-Content-Type-Options: nosniff`
- `Referrer-Policy: strict-origin-when-cross-origin`
- `Permissions-Policy` — denies sensor / camera / mic / geo / payment / USB
- `X-Frame-Options: DENY` — redundant with CSP `frame-ancestors` but pinned for older browsers

Set via `OnStarting` so headers land on every response (incl. 4xx/5xx that short-circuit the pipeline).

#### 10.5 — ScheduledEvent system-actor audit

[`ScheduledEventHandler`](src/Muster.Bot/Tracking/Handlers/ScheduledEventHandler.cs) now records audit rows on every auto-open / auto-close (Discord event went Active / Completed / Canceled). Actor = `0` (system), origin = `AuditOrigin.System`, payload = `{ eventId, eventName, channelId }`. New audit keys: `track.session.scheduledEventOpen` / `track.session.scheduledEventClose`. Admins can trace "where did this session come from?" without a human in the audit row.

#### 10.6 — `/muster create` re-tier

Slash command moved from `RequiredRole.Admin` → `RequiredRole.EventOfficer`. Admin still passes via the lockout-proof bypass. Event-officer staff can now run reaction check-ins without needing admin.

#### 10.7 — Audit retention pinned

[`AuditRetentionOptions.RetentionDays`](src/Muster.Infrastructure/Services/Platform/AuditPruneService.cs) defaulted to 90 (good) but wasn't surfaced in `appsettings.json`. Now explicit in both `Muster.Web` and `Muster.Bot` settings — ops can tune per-environment.

#### Tests
- 488 pass across all projects (no pre-existing fails remaining).
- Solution build clean.

### ✅ Phase 11 — Public space scaffolding

Public web surface gained the pages a v1 Discord-bot site needs. Auth/role plumbing unchanged — these are content
pages on `PublicLayout`.

#### New pages
| Route | Purpose | Notes |
|---|---|---|
| `/terms` | Terms of Service | Boilerplate template; jurisdiction + venue left as placeholders for legal review |
| `/support` | Support / contact | GitHub issues + private security advisory flow; URLs from config |
| `/faq` | FAQ | 10 high-level questions, no internal implementation details |
| `/features` | Feature wireframe | Cards for each main capability (tracking / quests / musters / wallets / seasons / roles / audit / API / web admin / multi-tenant) |
| `/changelog` | Release notes | High-level summaries for v0.3 / v0.4 / v0.5 — internals not leaked |
| `/brand` | Brand & press kit | Mark + lockup downloads, usage guidelines, one-line description |

#### Home page (`/`)
- Added gated **&ldquo;Add Muster to your server&rdquo;** primary CTA. Hidden behind `Public:InvitesEnabled` config flag; until v1.0 launch, renders a disabled placeholder button labelled "coming at v1.0". Flip the flag + set `Public:InviteUrl` to go live without code change.
- Added explore-links subtitle pointing to `/features`, `/faq`, `/changelog`.

#### Layout
- `PublicLayout` topbar gains Features / FAQ / Support links.
- Footer expanded from one-link to four columns: Product / Resources / Legal / brand line.

#### Static infrastructure
- [`robots.txt`](src/Muster.Web/wwwroot/robots.txt) — allow root, disallow `/guilds/`, `/api/`, `/account/`, `/onboarding`, `/Error`, `/health`, `/alive`. Sitemap URL pinned to `musterbot.com`.
- [`sitemap.xml`](src/Muster.Web/wwwroot/sitemap.xml) — public pages at musterbot.com with reasonable changefreq + priority.

#### Config additions ([appsettings.json](src/Muster.Web/appsettings.json))
```jsonc
"Public": {
  "InvitesEnabled": false,                       // flip to true at v1.0 launch
  "InviteUrl": "",                                // Discord OAuth invite URL
  "SupportUrl": "https://github.com/CODEdire/Muster/issues",
  "SourceUrl": "https://github.com/CODEdire/Muster"
}
```

#### Deferred (per request)
- Documentation page (`/docs`) — landing at v1.0
- Status page (`/status`)
- About / Pricing
- Final ToS jurisdiction + venue (needs legal review)
- Privacy policy finalisation (still draft per Phase 6 note)
- Add-to-server URL itself (set `Public:InviteUrl` at v1.0)

#### Tests
- 488 pass, solution build clean, no regressions.

### Phase 5 — Defence-in-depth

- Confirm cookie-protected OAuth `SaveTokens` payload (Discord access tokens) — currently in data-protected cookie; document.
- Audit: hide officer/admin nav links from members; today the redirect is what stops them, links may still render.
- Document the role model in `docs/web-and-auth.md` (replace the three-role table).

## Notes

- Lockout-proof bypass (guild owner) must survive every change — never gate behind a role that the owner doesn't implicitly hold.
- Resource authorizers (`ICurrencyAuthorizer`, `IQuestAuthorizer`, new `ITrackingAuthorizer`) are the single source of truth for owner-vs-staff rules. UI button visibility uses the same `Allows` method as command-handler enforcement — no drift.
- API-key actor binding (`ActsAsUserId`) is the strongest control: removing the actor's role or kicking them neutralizes the key instantly. Preserve this property in Phase 3.
