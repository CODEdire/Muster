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
VoiceStateUpdate ──► publish MemberParticipated ──► handler writes LedgerEntry (+ outbox)
ReactionAdd       ──► publish MemberParticipated ──► handler writes LedgerEntry (+ outbox)
/award (web/bot)  ──► send    AwardCurrency      ──► handler writes LedgerEntry (+ outbox)
```

Because scoring lives in handlers, the same logic runs whether triggered from the bot, the
web UI, or (later) an external connector.

## Contracts

Message contracts live in `Muster.Contracts/Messages.cs` and are intentionally
**broker-agnostic** (plain records, no transport types):

| Message | Direction | Purpose |
| --- | --- | --- |
| `MemberParticipated` | bot → handler | rewardable participation occurred |
| `AwardCurrency` | web/bot → handler | award currency (manual, mission, muster) |
| `AdjustCurrencyBalance` | connector → handler | mint/spend a spendable currency |
| `LedgerEntryRecorded` | handler → outbound | emitted after a ledger entry commits |

## v1: no broker

In v1 each container runs its own Wolverine instance against the **shared SQL database**.
There is no external broker; services integrate through the database and the durable
outbox. This honors the "small footprint" goal.

> **Note on SQL Server:** Wolverine's *database-as-queue* delivery is a PostgreSQL/Marten
> capability. On SQL Server the durable store provides the outbox/inbox, but actual
> **cross-process** delivery requires an external transport. We stay on EF Core / SQL Server
> and defer that transport.

## Later: Azure Service Bus toggle

When bot ↔ web decoupling or scale-out is needed, enable the **Azure Service Bus** transport
in the Wolverine configuration and add the resource to the AppHost. Because handlers consume
the broker-agnostic contracts, no handler code changes — only wiring. Aspire has a
first-class Service Bus integration for this.

## Sagas & scheduled messages

Wolverine sagas and scheduled (delayed) messages drive time-based workflows instead of
ad-hoc timers:

- **Auto-close** a tracking session at its scheduled end.
- **Expire** reaction musters at `ExpiresAt`.
- **Remind** participants ahead of an event op.
- **Roll over** seasons: archive the active season and open the next at the boundary.
