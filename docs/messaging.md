# Messaging & CQRS (Wolverine)

Muster uses **Wolverine** as its in-process command/query bus, HTTP endpoint framework
(Wolverine.HTTP), and durable messaging layer. This keeps the bot thin, centralizes
business rules in handlers, and makes reliable event publishing a first-class concern.

## Roles

- **Command/query bus** — Discord gateway events and admin/web actions become Wolverine
  **commands**. Handlers (in `Muster.Infrastructure`) own the business logic: award the
  ledger, open/close tracking sessions, approve quests, adjust wallets.
- **Wolverine.HTTP** — the public/CQRS API endpoints under `/api/v1` are authored as
  handlers rather than controllers. See [api.md](./api.md).
- **Durable outbox/inbox** — Wolverine's EF Core + SQL Server integration commits outgoing
  messages in the **same transaction** as the ledger write, then delivers with retries.
  This replaces any hand-rolled outbox table.

## Why the bot stays thin

The bot's job is to translate gateway events into messages, not to embed scoring rules:

```
VoiceStateUpdate ──► publish MemberParticipated ──► handler writes CurrencyLedgerEntry (+ outbox)
ReactionAdd       ──► publish MemberParticipated ──► handler writes CurrencyLedgerEntry (+ outbox)
/currency mint    ──► invoke  AdjustCurrency     ──► handler writes CurrencyLedgerEntry (+ outbox)
```

Because scoring lives in handlers, the same logic runs whether triggered from the bot, the
web UI, or (later) an external connector.

## Contracts

Message contracts live in `Muster.Contracts/` (split by feature: `CurrencyMessages.cs`,
`QuestMessages.cs`, `Commands.cs`) and are intentionally **broker-agnostic** (plain records, no transport types):

| Message | Direction | Purpose |
| --- | --- | --- |
| `MintCurrency` / `SpendCurrency` | connector → handler | machine-inbound mirror of an external movement (`CurrencyChangeResult`) |
| `TransferCurrency` / `AdjustCurrency` | bot/web/api → handler | user/staff CQRS (`IGuildCommand`, authorized + audited → `Result`) |
| `CurrencyMovementRecorded` | service → outbound | published on every staged ledger movement (the money-moved seam) |
| `RunCurrencyBulkAdjust` | web → background worker | apply a queued staff bulk mint/adjust to many members (durable, idempotent per member leg) |
| `SyncGuildMembers` | web → bot | pull the guild roster from Discord and upsert it (web equivalent of `/syncmembers`) |
| `QuestLifecycleNotified` | service → outbound | a quest changed state (Discord/connector fan-out) |

## Transport: Azure Service Bus

Cross-host messages travel over **Azure Service Bus**. The durable store (outbox/inbox) stays on SQL
Server so a ledger write and the messages it cascades commit atomically; a background relay then
forwards staged messages to the broker with retries.

The transport switches automatically by environment:

| Environment | Auth | Configuration source |
| --- | --- | --- |
| **Development** | emulator SAS (built into the container) | `ConnectionStrings:messaging` from the Aspire `messaging` resource (`RunAsEmulator`) |
| **Staging / Production** | managed identity (`DefaultAzureCredential`) | `Azure:ServiceBus:FullyQualifiedNamespace` from `AsExisting(...)` binding to the live namespace |

With neither set the bus runs purely in-memory (integration tests, local dev without a database).

> **Provisioning + RBAC:** Wolverine owns all topology — application topics + subscriptions (from
> conventional routing) and its own operational queues (per-node response, per-host retry, shared
> DLQ). The AppHost (`MessagingExtensions`) only spins up the namespace and, in run mode, the
> Microsoft Service Bus emulator container. Wolverine reaches the emulator's separate management
> plane via the second connection string the AppHost wires through (`ConnectionStrings:messaging-management`,
> built from the emulator's `emulatorhealth` endpoint on container port 5300). Against the live
> namespace the same `AutoProvision()` call works through managed identity — no second connection
> string needed. The runtime workload identity needs **Azure Service Bus Data Owner** on the
> namespace so it can create topics/queues/subscriptions on first connect.

## Topology & conventional routing

The set of cross-host contracts lives in `src/Muster.Contracts/MessageRouting.cs` as a single list
(`MessageRouting.CrossHostMessages`). It exists so the conventional-routing convention can be filtered
to just the contracts that travel cross-host — without that filter Wolverine would also auto-route
every local `IGuildCommand` and every discovered NetCord gateway event to a remote topic.

Wolverine resolves topology at runtime via `UseTopicAndSubscriptionConventionalRouting(...)`:
every message published flows to its topic; every handler discovered in the host assembly
auto-subscribes. Bot/web pass only the host identifier — handler presence IS the listen contract,
no per-flow opt-in flags.

Naming convention:

- **Topic** = `muster.` + kebab-case of the message type name (`PrefixIdentifiers("muster")` on the
  transport + `MessageRouting.TopicName(type)` for the bare form).
- **Subscription** = host's Wolverine `ServiceName` (`HostNames.Bot` = `"bot"`, `HostNames.Web` = `"web"`).
  A topic with two listening hosts has two clearly named subs in the portal.

| Contract | Topic | Subscriptions (driven by handler discovery) |
| --- | --- | --- |
| `QuestLifecycleNotified` | `muster.quest-lifecycle-notified` | `bot` (Discord channel board), `web` (push-updated quest views) |
| `CurrencyMovementRecorded` | `muster.currency-movement-recorded` | `bot` (`CurrencyDmHandler` + `CurrencyWebhookHandler`) |
| `SyncGuildMembers` | `muster.sync-guild-members` | `bot` (`GuildMemberSyncHandler`) |
| `SessionAttendanceChanged` | `muster.session-attendance-changed` | `web` (`SessionUpdateNotifierHandler`) |

Pruning checkpoints bypass `StageAsync`, so the currency-movement firehose excludes them.

Adding a new cross-host message: write the contract record in `Muster.Contracts`, add its type to
`MessageRouting.CrossHostMessages`, and write the handler in the listening host's assembly. Wolverine
provisions the topic + subscription on next boot.

## Implementation

- `WolverineExtensions.AddMusterMessaging(builder, hostName)` wires Wolverine into every host:
  - Sets `opts.ServiceName = hostName` (drives the conventional subscription name).
  - Always enables `UseEntityFrameworkCoreTransactions` so Wolverine inlines the `DbContext` into handler codegen.
  - If `ConnectionStrings:musterdb` is set: `PersistMessagesWithSqlServer(connStr, "muster")` (durable outbox/inbox).
  - Picks the transport — emulator connection string takes priority, then live namespace + `DefaultAzureCredential`,
    else stays in-memory.
  - If `ConnectionStrings:messaging-management` is set (emulator only — the AppHost wires it from the
    `emulatorhealth` endpoint on container port 5300), assigns it to `transport.ManagementConnectionString`
    so Wolverine's admin client can reach the emulator's management plane.
  - Applies the transport prefix via `bus.PrefixIdentifiers("muster")`, calls `bus.AutoProvision()`, and
    enables `UseTopicAndSubscriptionConventionalRouting` with `IncludeTypes` filtered to the cross-host
    set and the naming callbacks bound to `MessageRouting.TopicName` + `hostName`.
- Handlers live in `Muster.Infrastructure.Messaging` (cross-host effects) or the host assembly (host-specific
  effects) and are discovered via `opts.Discovery.IncludeAssembly`. Currency mutations flow through
  `ICurrencyService`, which writes the ledger and **publishes `CurrencyMovementRecorded`** for every staged
  movement; `CurrencyMovementRecordedHandler` is the outbound subscription seam. `IGuildCommand`s
  (`TransferCurrency`/`AdjustCurrency`, quest commands) are audited centrally by `AuditMiddleware`.
- Because handlers are plain methods, they're unit-tested by calling them directly with an in-memory
  database — no bus required. Integration tests that build a host call `AddMusterMessaging(hostName: "test")`
  without either connection string and stay on the in-memory branch.

## Sagas & scheduled messages

Wolverine sagas and scheduled (delayed) messages drive time-based workflows instead of
ad-hoc timers:

- **Auto-close** a tracking session at its scheduled end.
- **Expire** reaction musters at `ExpiresAt`.
- **Remind** participants ahead of an event op.
- **Roll over** seasons: archive the active season and open the next at the boundary.
