# Transition: move quest side-effects onto Wolverine messages (outbox)

A handoff for a fresh session. It captures the agreed architecture and a concrete first slice. Read the
"Decision" and "Guardrails" before writing code — the point is a *pattern*, not a big-bang rewrite.

---

## Background / current state

- **Topology:** .NET Aspire AppHost runs **Bot** (NetCord gateway), **Web** (Blazor SSR), and a
  **MigrationService**, all referencing the shared **`Muster.Infrastructure`** library and one SQL
  Server database. There is also a **`Muster.Contracts`** project (good home for message types).
- **WolverineFx 6** is already wired via `src/Muster.Infrastructure/WolverineExtensions.cs`
  (`AddMusterMessaging`), called from both `Muster.Bot/Program.cs` and `Muster.Web/Program.cs`. Today
  it's used lightly (leader-only quest sweep coordination).
- **Code layout (recent refactor):** services live in `Services/<Feature>/` and command services in
  `Commands/<Feature>/`, each with aligned namespaces (`Muster.Infrastructure.Services.Quests`, etc.).
  EF lives under `Persistence/` (`Muster.Infrastructure.Persistence`, migrations in
  `Persistence/Migrations`).
- **Tests:** `tests/Muster.UnitTests` (currently 114 passing, InMemory EF). Build `Muster.slnx`.

### Domain glossary (so the messages model the right thing)
- **`MissionService`** (`Services/Quests`) — **guild quests** (`MissionOrigin.Guild`, reward *minted* on
  approval) and **event ops** (sign-up/attendance). Flow: claim → submit → manager approve (mints
  reward; closes at capacity unless repeatable).
- **`BountyService`** (`Services/Quests`) — **personal quests** (`MissionOrigin.Player`, reward
  *escrowed* from the poster at post time). Flow: post (hold escrow) → take → submit → owner
  confirm / finalize / dispute → arbitrate (escrow payout or refund). Intake approval + final-approval
  workflow lives here.
- Both operate on the same `Mission`/`MissionParticipant` tables; **`QuestBoardService`**
  (`Commands/Quests`) is the single entry point that routes each action by `Origin`.
- **Existing seams to replace:** `IQuestNotifier` (lifecycle notifications) and `IQuestRewardSink`
  (`QuestCompletion` → external reward resolution), both in `Services/Quests/QuestNotifier.cs`, both
  currently **logging stubs**. These are exactly the side-effects we want to make durable.

---

## Decision (agreed architecture — do not relitigate)

1. **Services stay the domain core.** Do **not** convert everything to handlers.
2. **Messages are the shared contract** the bot, web, and any future API speak. **Handlers are thin
   wrappers** that call services; services own the logic and the DB.
3. **Commands** (state changes) can be invoked in-process via `bus.InvokeAsync<CommandResult>(cmd)` so
   edges still get an immediate reply; the same call can route over a transport later without changing
   callers. **Reads stay direct service calls** — do not message queries.
4. **Side-effects become published events via the transactional outbox** (durable, retried,
   dead-lettered): **audit, notifications, external reward propagation.**
5. **Authoritative writes stay synchronous in the command transaction.** The internal ledger
   (`AwardService`/escrow) is the source of truth and is read immediately, so keep it in-transaction.
   Only *downstream* effects go async.
6. **Idempotency is what makes async safe.** The ledger already enforces it via a unique
   `(SourceType, SourceId)` index, so redelivered awards no-op. New event handlers must be idempotent
   too (or naturally safe to repeat).

---

## First slice (build this)

**Goal:** when a quest settles, publish a durable `QuestCompleted` event through the outbox and let
independent handlers react — establishing the copyable pattern.

1. **Define the event** (in `Muster.Contracts`), e.g. `QuestCompleted(ulong GuildId, Guid MissionId,
   string MissionName, ulong CompleterUserId, MissionOrigin Origin, Guid RewardCurrencyId,
   long RewardAmount, long BonusPoints, QuestTier Tier)`. Reuse the shape of the existing
   `QuestCompletion` record.
2. **Publish it transactionally** at the completion chokepoints, replacing the in-line
   `IQuestRewardSink`/`IQuestNotifier` "settled" calls:
   - `MissionService.ApproveAsync` (guild quest mint),
   - `BountyService.PayAndBonusAsync` / `CompletedAsync` (used by `ConfirmAsync`, `FinalizeAsync`,
     `ArbitrateAsync` pay paths) — and therefore the auto-resolve sweep, which goes through these.
   Use Wolverine's EF/SQL **transactional outbox** so the publish commits with `SaveChangesAsync`.
3. **Add consumers (thin handlers):**
   - `RecordAuditHandler` → writes the audit entry (today done inline / by the sweep).
   - `QuestNotificationHandler` → the lifecycle notification (keep logging until Discord delivery is
     wired; this is where the real Discord notifier lands later, in the **Bot** host).
   - `ExternalRewardHandler` → the `IQuestRewardSink` replacement (logging until the connector exists).
4. **Keep the broader lifecycle notifications** (`PendingApproval`, `Accepted`, `Submitted`,
   `RevisionRequested`, `AwaitingFinalApproval`, `Disputed`, `Refunded`, `RejectedAtIntake`) — decide
   whether to publish those as events now too, or leave them on `IQuestNotifier` for a follow-up. The
   *settled/reward* path is the must-do; the rest can follow the same pattern incrementally.

**Optional second slice (reference command pattern):** convert "post quest" to a `PostQuest` command +
handler wrapping `QuestBoardService.PostAsync`, and have the bot module and the web post page both do
`bus.InvokeAsync<CommandResult>(new PostQuest(...))`. This demonstrates "both edges, one contract,
in-process today."

---

## Guardrails / acceptance criteria

- **Don't break the 114 existing tests;** add coverage for the new handlers and for outbox publish.
- Configure Wolverine's **EF Core transactional outbox/inbox** against `MusterDbContext`; the outbox
  tables need an EF migration (place it in `Persistence/Migrations`,
  `Muster.Infrastructure.Persistence.Migrations`; use `--output-dir Persistence/Migrations` if the
  tooling defaults elsewhere).
- **Authoritative ledger writes remain synchronous** — verify balances are still correct immediately
  after settle (no "paid but balance shows 0" window).
- Handlers are **idempotent**: a redelivered `QuestCompleted` must not double-audit/double-mint.
- **Host placement:** the Discord-delivery notification handler belongs in the **Bot** host; the
  external reward connector can be a worker/whichever host owns that integration. Keep the publish in
  the shared library so any host can emit.
- Build `Muster.slnx` clean; `dotnet test tests/Muster.UnitTests` green. Boot Web + AppHost to confirm
  startup. Local SQL now persists (Aspire data volume), so migrations apply once and stick.
- Keep changes a **vertical slice**, not a sweep across all services. Land it, prove the pattern, stop.

## Useful commands
- Build: `dotnet build Muster.slnx`
- Test: `dotnet test tests/Muster.UnitTests/Muster.UnitTests.csproj`
- Migration: `dotnet ef migrations add <Name> --project src/Muster.Infrastructure/Muster.Infrastructure.csproj`
- Offline model check: `dotnet ef migrations has-pending-model-changes --project src/Muster.Infrastructure/...`

---

## Prompt for the new session (paste this)

> You are continuing work on **Muster** (.NET 10 / Aspire; Bot + Web + MigrationService over a shared
> `Muster.Infrastructure` library and one SQL DB; WolverineFx 6 already wired via `AddMusterMessaging`).
>
> **Task:** Introduce the "messaging at the seams" pattern by moving quest **completion side-effects**
> onto Wolverine messages backed by the **transactional outbox**, without rewriting the service layer.
>
> Read `transition.md` and `docs/features/guild-quests.md` first. Implement the **First slice**:
> 1. Add a `QuestCompleted` event in `Muster.Contracts` (mirror the existing `QuestCompletion` record).
> 2. At the completion chokepoints (`MissionService.ApproveAsync` and `BountyService` pay paths used by
>    Confirm/Finalize/Arbitrate), publish `QuestCompleted` **via Wolverine's EF transactional outbox**
>    so it commits with `SaveChangesAsync`. Replace the inline `IQuestRewardSink`/`IQuestNotifier`
>    "settled" calls.
> 3. Add thin, **idempotent** handlers: audit, notification (logging stub, Bot host), external reward
>    (logging stub). Generate the EF migration for Wolverine's outbox tables under
>    `Persistence/Migrations`.
>
> **Constraints:** authoritative ledger writes stay synchronous in the command transaction; reads stay
> direct service calls (don't message queries); keep it a vertical slice; don't break the 114 existing
> tests and add new ones (publish-on-settle + idempotent redelivery). Build `Muster.slnx` clean and run
> `dotnet test`. Commit on the designated feature branch and summarize the pattern so the team can
> replicate it for Audit/Ledger/Rewards.
>
> Confirm the outbox wiring approach (Wolverine + EF Core + SQL Server) before building, and flag any
> place where going async would create a user-visible eventual-consistency gap.
