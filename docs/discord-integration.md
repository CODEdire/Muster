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
| `MessageContent` | **not used** — counting needs only metadata | privileged |
| `GuildMembers` | full member roster sync | privileged |

v1 avoids privileged intents: message **counts** don't need `MessageContent`, and member
data is upserted lazily on interaction instead of requiring `GuildMembers`. Full roster sync
is a documented post-v1 toggle (requires Discord approval beyond 100 guilds).

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
| `/award user\|voice\|reacted` | admin | manual / bulk awards |
| `/score me\|user` | all | view a member's score |
| `/leaderboard` | all | season leaderboard |
| `/wallet` | all | currency balances |
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

- **Command services** (`Muster.Infrastructure.Commands`, e.g. `AwardCommandService`,
  `ScoreCommandService`, `TrackingCommandService`) hold all validation, orchestration, and
  message formatting. They take primitives (guild id, user id, parsed parameters) and return
  a platform-independent `CommandResult { Message, IsError }`.
- **NetCord modules** (`Muster.Bot/Modules`) are adapters: they pull ids/parameters off
  `Context`, handle Discord-specific concerns (e.g. "must be used in a server"), call the
  command service, and return `result.Message`.
- **Gateway handlers** (`Muster.Bot/Handlers`) follow the same shape: extract primitives from
  the event payload and delegate to a service (`TrackingSessionService.ProcessVoiceStateAsync`,
  `MusterService.RecordReactionAsync`).

This means commands and event handling are exercised in unit tests against an in-memory
database — validation, idempotency, capacity limits, leaderboard/wallet formatting — with no
Discord connection. The same command services are reusable by the web UI and API.

## Caveats

- **Backfill**: the bot cannot see activity before it joins a guild.
- **Redelivery**: gateway RESUME can redeliver events; idempotency indexes
  (`SourceMessageId`, `(SourceType, SourceId)`) prevent double-counting.
- **Rate limits**: handled by NetCord; avoid chatty per-event REST calls.
