# Web UI & Authentication

`Muster.Web` is an ASP.NET Core app hosting a **Blazor static SSR** UI and the
Wolverine.HTTP API.

## Render mode

- **Static server-side rendering** with streaming rendering and enhanced
  navigation/forms. **No interactive Server or WebAssembly render mode — no SignalR.**
- All mutations go through standard **form posts** or API calls handled by Wolverine, not
  interactive component callbacks. This keeps the web tier stateless and cheap to scale.

The root component is `Components/App.razor`; routing is in `Components/Routes.razor`.

## Authentication — Discord OAuth

Login uses Discord OAuth2 via `AspNet.Security.OAuth.Discord` with a cookie session:

- Default scheme: **cookie**; challenge scheme: **Discord**.
- Scopes: `identify`, `guilds` (to resolve which guilds the user belongs to and their
  guild-level permissions).
- `ClientId` / `ClientSecret` come from configuration (`Discord:ClientId`,
  `Discord:ClientSecret`) — user-secrets locally, Key Vault in Azure.

## Authorization

Three application roles, resolved per guild:

| Role | Who | Can |
| --- | --- | --- |
| **SuperAdmin** | bot host operator | manage all guilds, global settings |
| **GuildAdmin** | guild owner, Manage-Guild holders, or a configured admin role | configure the guild, manage missions/musters/seasons, issue awards |
| **Member** | any guild member | view leaderboards, own score/wallet, claim quests, RSVP |

Guild-admin status is resolved by `GuildAuthorizationService` with a **lockout-proof bypass** — a
member is admin if ANY of these hold, so a bad role mapping can never lock everyone out:

1. they are the **guild owner** (`Guild.OwnerId`);
2. they hold a Discord role with **Administrator** or **Manage Guild** permission (from the synced
   `GuildRole` snapshot);
3. they hold a role configured in `GuildSettings.AdminRoleIds`.

Officer additionally includes `OfficerRoleIds`. The same service gates the bot's mutating slash
commands (via `MusterModuleBase`) and will back web authorization. The role mapping is configured with
`/config-admin-role` / `/config-officer-role` (the owner can always run these).

## Pages (v1 target)

- Guild dashboard; season leaderboards
- Tracking-session management (open/close, Discord-event-bound sessions, voice attendance)
- Quest board + **approval queue**; event-op management
- Reaction-muster management
- Manual/bulk award console
- Season management; currency configuration
- Guild settings / reward configuration
- Member detail (wallets across currencies); audit log
- API client management

## Security notes

- Antiforgery is enabled for all form posts (`UseAntiforgery`).
- API keys for external connectors are shown once and stored **hashed** (`ApiClient`).
- Treat OAuth tokens as secrets; `SaveTokens` is enabled to call Discord on the user's
  behalf where needed (e.g. listing guilds).

## Local OAuth setup

The web app is pinned to **https://localhost:7443** (`Muster.Web/Properties/launchSettings.json`) so the
OAuth redirect URI is stable. To enable login locally:

1. In the Discord Developer Portal → your app → **OAuth2 → Redirects**, add
   **`https://localhost:7443/signin-discord`** (the `AspNet.Security.OAuth.Discord` callback path).
2. Ensure the AppHost user-secrets have `Parameters:discordClientId` and
   `Parameters:discordClientSecret` (see `local-dev.md`); the AppHost injects them into the web app.
3. Run via the AppHost. `/account/login` issues the Discord challenge; `/account/logout` clears the cookie.

> If the Aspire dashboard shows the web app on a different host/port than 7443, add that
> `<host>/signin-discord` as an additional redirect URI (Discord allows several).
