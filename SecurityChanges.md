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

### Phase 5 — Defence-in-depth

- Confirm cookie-protected OAuth `SaveTokens` payload (Discord access tokens) — currently in data-protected cookie; document.
- Audit: hide officer/admin nav links from members; today the redirect is what stops them, links may still render.
- Document the role model in `docs/web-and-auth.md` (replace the three-role table).

## Notes

- Lockout-proof bypass (guild owner) must survive every change — never gate behind a role that the owner doesn't implicitly hold.
- Resource authorizers (`ICurrencyAuthorizer`, `IQuestAuthorizer`, new `ITrackingAuthorizer`) are the single source of truth for owner-vs-staff rules. UI button visibility uses the same `Allows` method as command-handler enforcement — no drift.
- API-key actor binding (`ActsAsUserId`) is the strongest control: removing the actor's role or kicking them neutralizes the key instantly. Preserve this property in Phase 3.
