# Discord Integration (NetCord)

The bot (`Muster.Bot`) uses **NetCord** for the gateway connection and interactions. It is
a worker service wired via `Muster.ServiceDefaults` and shares `Muster.Infrastructure` with
the web app.

> **Status:** M0 wires the gateway registration (`AddDiscordGateway`). Slash-command
> modules and gateway event handlers land in M2/M3. Pin a mutually compatible NetCord
> package set (core / `NetCord.Hosting` / `NetCord.Services`) when adding commands.

## Gateway intents

| Intent | Why | Privileged? |
| --- | --- | --- |
| `Guilds` | guild lifecycle, channels, scheduled events | no |
| `GuildVoiceStates` | voice attendance (primary reward signal) | no |
| `GuildMessageReactions` | reaction musters, check-ins, op/quest responses | no |
| `GuildScheduledEvents` | bind tracking sessions to native Discord events | no |
| `GuildMessages` | stats-only message activity (counts) | no |
| `GuildUsers` (Server Members) | member join/leave/nickname/role sync | **privileged** |
| `MessageContent` | **not used** — counting needs only metadata | privileged |

We use the **Server Members** privileged intent (`GuildUsers`) so the local `DiscordUser` /
`GuildMember` tables stay in sync with Discord. Enable **Server Members Intent** in the Discord
Developer Portal (Bot settings); below 100 guilds this needs no approval. We still avoid
`MessageContent` — message activity is counted from metadata only.

### Member & guild sync

- **Lazy upsert:** `MessageActivityHandler`, `MusterReactionHandler`, and `VoiceAttendanceHandler`
  upsert the acting member via `MemberSyncService`, so anyone who chats/reacts/joins voice is recorded.
- **Lifecycle:** `MemberLifecycleHandler` handles `GuildUserAdd`/`GuildUserUpdate` (upsert nickname +
  role snapshot) and `GuildUserRemove` (drop the membership; the shared user + ledger history persist).
- **Guild:** `GuildLifecycleHandler` handles `GuildCreate` (provision) and `GuildUpdate` (rename/icon).
- **Authorization by role mapping:** `GuildAuthorizationService` derives admin/officer status by
  intersecting a member's `RoleIds` with `GuildSettings.AdminRoleIds` / `OfficerRoleIds` — Discord
  roles, not individual users. Re-syncing role snapshots keeps these answers current.
- **Participant gate:** `GuildSettings.ParticipantRoleIds` controls who can earn rewards / be tracked.
  Empty (default) = everyone participates; setting roles restricts earning to org members and excludes
  guests/newcomers. Enforced at muster reactions, quest claims, op sign-ups, and voice-attendance
  rewards (attendance is still recorded, just not rewarded for non-participants). Configure via
  `/config-participant-role` or the web role-mapping page.

## Install flow

Invite the bot with an OAuth2 URL carrying the `bot` and `applications.commands` scopes plus
minimal permissions (read messages, add reactions, manage events, optionally manage roles
later for rank rewards). On join, `GuildCreate` upserts the `Guild` row and seeds defaults
(POINTS currency, an initial season); `GuildDelete` marks it inactive.

## Slash commands (v1 target)

| Command | Who | Purpose |
| --- | --- | --- |
| `/ping` | all | liveness |
| `/track start\|stop\|status` | admin | open/close a manual voice tracking session |
| `/quest post\|claim\|submit\|approve\|list` | mixed | quest board lifecycle |
| `/op create\|signup\|close` | mixed | scheduled event ops (RSVP/attendance) |
| `/muster create` | admin | post a reaction check-in |
| `/currency balance\|history\|list\|give\|notify\|inspect\|mint\|adjust` | mixed | wallet balances + history, currency directory, member transfers, DM-receipt opt-out, staff member-wallet inspect + mint/adjust (CQRS) |
| `/leaderboard` | all | season leaderboard |
| `/season start\|end\|status` | admin | manage seasons |
| `/config` | admin | guild settings / reward config |

Register as **guild** commands in development (instant updates) and **global** commands in
production (~1h propagation).

## Event handling

| Gateway event | Action |
| --- | --- |
| `VoiceStateUpdate` | accumulate `VoiceAttendance` for active in-scope sessions (primary reward) |
| `MessageReactionAdd/Remove` | muster, quest/op responses, session check-in |
| `GuildScheduledEvent Create/Update/Delete` | auto open/close bound tracking sessions |
| `MessageCreate` | stats-only `ActivityRecord` (+ dedupe via `SourceMessageId`) |
| `GuildMemberUpdate` | role snapshot sync |

Handlers translate events into Wolverine commands (see [messaging.md](./messaging.md)) so
scoring rules live in one place.

## Testability: thin adapters over command services

Discord-facing code is kept as thin as possible so the real logic can be tested without a
gateway:

- **Command services** (`Muster.Infrastructure.Commands`, e.g. `TrackingCommandService`,
  `ConfigCommandService`) hold validation/orchestration for the non-CQRS commands and return a
  platform-independent `CommandResult { Message, IsError }`. Currency + quest actions instead go
  through **Wolverine `IGuildCommand`s** dispatched on the bus (authorized + audited centrally).
- **NetCord modules** (`Muster.Bot/Modules`) are adapters: they pull ids/parameters off
  `Context`, handle Discord-specific concerns (e.g. "must be used in a server"), call the
  command service, and return `result.Message`.
- **Gateway handlers** (`Muster.Bot/Handlers`) follow the same shape: extract primitives from
  the event payload and delegate to a service (`TrackingSessionService.ProcessVoiceStateAsync`,
  `MusterService.RecordReactionAsync`).

This means commands and event handling are exercised in unit tests against an in-memory
database — validation, idempotency, capacity limits, leaderboard/wallet formatting — with no
Discord connection. The same command services are reusable by the web UI and API.

## Time input

Discord sends slash-command text options with **no timezone** — the bot interprets them. `TimeZoneService.ParseLocalAsync`
accepts three forms: a Discord timestamp (`<t:1700000000>`), a bare unix value, or a relative duration (`in 3 days`,
`2h`) — all zone-independent. A plain wall-clock date (`2026-06-01 18:00`) is read in the user's **own** zone; if they
haven't set one the command is **gated** with a message pointing to `/timezone` (autocompleted from the .NET system
zone list) or the timestamp/relative forms. The web equivalent is the `/onboarding` page, which a member with no zone
is redirected to before any guild page (`GuildPageComponentBase`). Zones are stored as IANA ids (Windows ids are
converted), so they're consistent across the bot and web.

## Caveats

- **Backfill**: the bot cannot see activity before it joins a guild.
- **Redelivery**: gateway RESUME can redeliver events; idempotency indexes
  (`SourceMessageId`, `(SourceType, SourceId)`) prevent double-counting.
- **Rate limits**: handled by NetCord; avoid chatty per-event REST calls.
