# Gotchas / Backburner

Known limitations and deferred items. Not bugs — things parked with a reason.

## SQLite test provider (Muster.Persistence.UnitTests)

The unit suite runs on in-memory SQLite (no Docker). Some real queries/constraints can't be
faithfully exercised there and are deferred to a SQL-Server (Testcontainers) suite:

- **DateTimeOffset `ORDER BY` and `</<=` don't translate.** SQLite stores DateTimeOffset as TEXT.
  This rules out the Mission list/sweep queries: `ListQuestBoardAsync`, `ListScheduledDueAsync`,
  `ListExpiredGuildQuestsAsync`, `ListExpiredPersonalQuestsAsync`, `ListStaleClaimsAsync`,
  `ListStaleSubmissionsAsync` (any `OrderBy(ScheduledStart ?? CreatedAt)` or deadline/cutoff compare).
- **Unique index + NULL semantics differ.** SQLite treats NULLs as distinct in a unique index;
  SQL Server treats them as equal (one NULL allowed). Wallet/Ledger scope tests use a non-null
  `SeasonId` to exercise the collision; the null-scoped SQL-Server behaviour is untested here.
- **`RowVersion` concurrency token** is gated to `Database.IsSqlServer()` — SQLite/InMemory skip it,
  so optimistic-concurrency conflicts aren't covered by unit tests.
- **`decimal(20,0)`-mapped ulong keys** — the SQL-Server-specific mapping isn't validated under SQLite.

→ When a Testcontainers MsSql job exists, port the above into it.

## Quest lifecycle publish — transactional atomicity

Quest command handlers run with Wolverine's `UseEntityFrameworkCoreTransactions` (always on), so the
`QuestLifecycleNotified` publish *should* commit with the EF state write via the durable outbox. What's tested:

- **Event is emitted on the command path** — `QuestCommandHandlerTests.Approve_PublishesSettledLifecycleEvent`
  asserts the transition publishes its lifecycle event (recording bus).
- **Real-bus pipeline commits** — `AuditMiddlewareTests` invokes a command through a live Wolverine host and
  asserts the EF write (wallet + audit row) committed.

**Not tested (framework guarantee + needs SQL):** strict rollback atomicity — that a handler failure *after*
the state write leaves **neither** the DB change nor a published event. The durable outbox only exists against
SQL Server, and faithfully testing it needs Testcontainers MsSql + a forced mid-handler failure (largely
re-testing Wolverine). Deferred to the Testcontainers suite. Note: `QuestService` calls `SaveChangesAsync` +
`PublishAsync` itself rather than returning a cascading message — if we ever want belt-and-suspenders atomicity,
return the lifecycle event as a Wolverine cascading message from the handler instead.

## Currency connector secret at rest

A currency's outbound connector (`Currency.Connector`, owned JSON) stores its `Secret` — the webhook HMAC key
or the HTTP-API bearer/api-key — as **plaintext in the `Currencies.Connector` JSON column**. Same trust level
as the bot token in app config, and it's **never returned to the web client** (write-only: the admin UI shows
only *whether* a secret is set; a blank field on save keeps the existing one). Acceptable for v1; a later pass
should move connector secrets into a protected store (e.g. Key Vault / data-protection) rather than the row.
