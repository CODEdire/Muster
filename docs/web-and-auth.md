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

Guild-admin status is derived from the member's synced Discord roles (see
`GuildSettings.AdminRoleIds` / `OfficerRoleIds`) combined with Discord guild permissions.

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
