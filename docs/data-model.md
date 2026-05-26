# Data Model

All entities live in `Muster.Domain/Entities` and are mapped by `MusterDbContext`
(`Muster.Persistence`). Every record is **guild-scoped** (the tenant boundary). Discord
snowflake ids are stored as `ulong` (mapped to `decimal(20,0)` on SQL Server).

## Core

| Entity | Purpose |
| --- | --- |
| `Guild` | A Discord server (tenant). Owns `GuildSettings` (stored as JSON). |
| `GuildSettings` | Admin/officer role ids, tracked channels, quest-approval policy. |
| `DiscordUser` | A Discord user, shared across guilds. |
| `GuildMember` | A user's membership in a guild; role snapshot, nickname, join date. |

## Participation sources (append-only, audited)

| Entity | Purpose |
| --- | --- |
| `TrackingSession` | A bounded window where channel activity is rewardable. `Source` is `Manual` or `DiscordScheduledEvent`. Scoped to a voice channel. |
| `VoiceAttendance` | Accumulated voice minutes per member within a session — the **primary rewardable signal**. |
| `ActivityRecord` | Raw activity event (message/voice). **Stats-only in v1.** `SourceMessageId` is the dedupe key. |
| `DailyActivityRollup` | Per-(guild,user,channel,day) counts so stats/leaderboards stay cheap. |
| `GuildQuest` | A claimable board quest (claim → submit → approve). `Origin` is `Guild` (minted reward) or `Player` (escrowed bounty). |
| `QuestParticipant` | A member's state on a quest (Claimed/Submitted/Approved/Rejected/RevisionRequested). |
| `GuildEvent` | A scheduled op with sign-up/attendance (`Scheduled → Open → Closed`/`Cancelled`). May optionally link a `TrackingSession`. |
| `EventAttendee` | A member's state on an event (SignedUp/Attended/NoShow). |
| `ReactionMuster` | A react-to-check-in message. Multi-emoji (distinct responses), optional capacity (first N). |
| `ReactionParticipant` | A member's reaction on a muster. |
| `ManualAward` | Admin manual or bulk award for off-platform contributions. |

## Scoring, currency & seasons

The reward system is a **multi-currency, append-only ledger**.

| Entity | Purpose |
| --- | --- |
| `Season` | A scoring period. Points leaderboards are scoped to a season. |
| `Currency` | A guild currency. `POINTS` is seeded, seasonal, drives leaderboards. Spendable currencies (e.g. `COIN`) persist across seasons and are mint/spendable by connectors. |
| `LedgerEntry` | **Single source of truth.** Append-only signed `Amount` tagged with `CurrencyId`, optional `SeasonId`, `SourceType` and `SourceId`. |
| `Wallet` | Cached balance per `(guild, user, currency, season)`; rebuildable from the ledger. |

### Why a ledger

- **Auditability**: every point/coin movement is a row with its originating source.
- **Idempotency**: a unique filtered index on `(SourceType, SourceId)` guarantees a given
  participation event produces at most one entry, even under gateway redelivery.
- **Extensibility**: external "Coin" connectors mint/spend by appending entries
  (`SourceType = Connector`), and the same outbox publishes ledger events outward.

### Seasons vs. persistent currencies

- **Points** (`IsSeasonal = true`) reset/archive per season — leaderboards reflect the
  active season. `LedgerEntry.SeasonId` is set for these.
- **Spendable currencies** (`IsSeasonal = false`, `IsSpendable = true`) persist as a
  wallet across seasons; their ledger entries have `SeasonId = null`.

## Operations

| Entity | Purpose |
| --- | --- |
| `AuditLog` | Admin/config actions taken via the web UI. |
| `ApiClient` | A registered external connector; stores a **hashed** API key, scopes, guild. |

## Indexes & integrity (highlights)

- `Currency (GuildId, Code)` unique.
- `LedgerEntry (SourceType, SourceId)` unique, filtered on non-null `SourceId` — idempotency.
- `LedgerEntry (GuildId, UserId, CurrencyId, SeasonId)` — balance/leaderboard reads.
- `Wallet (GuildId, UserId, CurrencyId, SeasonId)` unique.
- `ActivityRecord (SourceMessageId)` unique, filtered — message dedupe.
- `VoiceAttendance (TrackingSessionId, UserId)` unique; `ReactionParticipant (MusterId, UserId)` unique; `EventAttendee (EventId, UserId)` unique.
- `QuestParticipant (QuestId, UserId)` **non-unique** — a member may hold several participations when a quest allows repeat completions.

## Migrations

The initial schema is `Migrations/*_InitialCreate`. Generate further migrations with:

```bash
dotnet ef migrations add <Name> --project src/Muster.Persistence
```

`MusterDbContextFactory` provides the design-time context (no database is contacted).
Migrations are applied in production by `Muster.MigrationService`, never auto-applied by
the bot or web.
