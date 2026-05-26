# GuildQuest — End-to-End Design Review

> **Status: ✅ FEATURE-COMPLETE.** All sections (§0–§7) shipped + tested (60 unit · 184 integration · 35 persistence).
> Remaining items are deferred features and optional polish (see **Wishlist**); the only open work is **live smoke
> testing**, which needs the app running. Reopen a section only if a smoke test surfaces something.

**Concept.** An anime-style guild quest board. A **GuildMaster** (quest manager / admin) sets reward
points, approves submissions, and arbitrates disputes. Members claim quests that are either:
- **Guild drive** — `Origin = Guild`: the guild *mints* the reward on approval. Posted by a GuildMaster.
- **Player bounty** — `Origin = Player`: funded by the poster's *own* currency (escrowed), settled to the completer.

This checklist surveys Domain/Backend, Authorization, Discord, API, UI, Tests, and Notifications for gaps and
logic issues, and tracks the work to make the feature solid end-to-end.

Severity: 🔴 correctness/security · 🟠 functional gap · 🟡 polish/cleanup · ❓ concept decision.

---

## 0. Architecture: CQRS alignment (foundational — shapes §1, §4, §5)

We're standardising on CQRS. The **currency** side already does it: command contracts in `Muster.Contracts`
(`MintCurrency`, `SpendCurrency`, `TransferCurrency`…), **thin static handlers** in `Muster.Infrastructure/Messaging/`
(`Handle(command, ICurrencyService, ct)` → `Result`), dispatched via
`IMessageBus.InvokeAsync`. The **quest** side does *not*: `QuestModule`/Blazor → `QuestBoardService` →
`QuestService` are direct DI calls, no command/query messages. This inconsistency is the root of several gaps below.

Target shape for quests (matching the currency convention):

- [x] **Done — quest command contracts** in `Muster.Contracts` (all 15). *Deviation:* one `PostQuest` carrying `QuestOrigin` instead of separate `PostGuildQuest`/`PostPlayerBounty` (one-command-routes-by-origin, as agreed).
- [x] **Decided — query side = CQRS-lite.** Commands via the bus; reads via a direct read interface (no bus). Mirrors currency.
  - [x] **Done — `IQuestReadService`** + DTOs (`QuestBoardItem`/`QuestBoardPage`/`QuestEditView`/`QuestListItem`) over `QuestQueries`. All adapter reads go through it.
  - [~] **Won't do — `IQuestService` `internal`.** Wolverine generates handler code in a separate assembly that *references* `IQuestService`, so it must stay public (same reason `QuestService` is public). Encapsulation is by convention: adapters use commands + `IQuestReadService`, never `IQuestService`.
  - [x] **Done — `MusterDbContext` removed from Blazor** (`Quests.razor`, `QuestEdit.razor`, `QuestPost.razor` all on `IQuestReadService`/commands).
- [x] **Done — thin static handlers** in `Messaging/` returning the `Result` envelope (presentation `Result→CommandResult` stays in adapters via `ToCommandResult`).
- [x] **Decided — result shape.** `Result`/`Result<T>` envelope; per-domain reason enums kept; one mapper per adapter.
- [x] **Done — `QuestBoardService` retired.** Validation + currency-resolution + origin-routing moved into the handlers; timezone date-parsing stays at the bot adapter (web passes UTC).
- [x] **Done — auth in the handler** via `IQuestAuthorizer` (the single funnel). Discord `RequiredRole` kept as defense-in-depth.
- [x] **Transactional publish — verified to the practical limit.** Tested: the command *emits* its `QuestLifecycleNotified` (recording-bus test) and the real-bus pipeline commits the EF write (`AuditMiddlewareTests`). Strict rollback-atomicity is a durable-outbox (SQL) framework guarantee — deferred to Testcontainers (documented in `gotchas.md`); the escape hatch is to return the event as a cascading message.
- [x] **Done (bot + web)** — both surfaces dispatch commands via `IMessageBus`; no direct `QuestBoardService` calls remain. **API surface is §4 (not yet built).**

---

## ✅ 1. Authorization — COMPLETE (centralized, resource-based)

*(Was: three surfaces checked auth in different places — Discord `RequiredRole` attributes, web `_isManager`
button-hiding, inconsistent `QuestBoardService` checks. §0 funnels every action through a command handler; the
**resource-based authorizer** — the .NET resource-auth pattern, host-agnostic with a `GuildActor` not a
`ClaimsPrincipal` — centralizes the rules. All resolved below.)*

- [x] **Done — `IQuestAuthorizer`** over `GuildAuthorizationService`, all role/origin/owner rules in one place (enum named `QuestPermission`, not `QuestAction`, to avoid the web's existing `QuestAction` record).
- [x] **Done — handlers load + authorize.** Single enforcement point for bot + web (+ API when built).
- [x] **Done — UI button visibility uses `IQuestAuthorizer.Allows`.** Extracted a pure rule (`Allows(actor, isManager, origin, ownerId, isTaker, permission)`) shared by `AuthorizeAsync` and `Quests.razor`'s `CanEdit`/`CanIntake`/`CanFinalize` — no rule duplication, no extra DB calls (uses the page's cached `_isManager`). (`ActionsFor`'s inline gates can move to `Allows` in the upcoming UI cleanup.)
- [x] **Done — Discord `RequiredRole.QuestManager`** kept as defense-in-depth.
- [~] (optional) bridge to ASP.NET policies for the API host — see *Wishlist → optional polish*.

**Bugs the authorizer/funnel must close (verify with tests):**
- [x] 🔴 Arbitrate — now manager-gated in the handler + service recusal (`ArbitrateQuestHandler`; tested).
- [x] 🔴 Approve — manager-gated in the handler (`ApproveQuestSubmissionHandler`; tested).
- [x] 🔴 `QuestService.ApproveAsync`/`RejectAsync` don't assert `Origin == Guild` — on a player bounty they'd mint + bypass escrow (double-pay). **Done** (origin guard added; covered by `QuestCommandHandlerTests`).
- [x] 🟡 `CancelQuestAsync` unused `actorId` **dropped** — auth is enforced in the `CancelQuest` handler via `IQuestAuthorizer`.

---

## ✅ §1 COMPLETE

Authorization is centralized in `IQuestAuthorizer` (pure `Allows` rule + async `AuthorizeAsync`), enforced in every command handler (the single funnel), reused by the web for button visibility, with Discord `RequiredRole` as defense-in-depth. All three split-enforcement bugs (Arbitrate / Approve service-level checks, Origin-guard) closed + tested; recusal on arbitration. **Only deferred item:** the optional ASP.NET `IAuthorizationService` policy bridge — belongs with the **§4 API host** (not built yet).

---

## 2. Domain / Backend (the state machine)

The core state machine is solid: guarded `TransitionTo`, idempotent participant-keyed awards, escrow legs +
status committed together, anti-staleness sweep. Issues found:

- [x] 🟡 Dead field `GuildQuest.RequiresApproval` — **done**: deleted; migration `DropQuestRequiresApproval` drops the column (approval policy lives in `GuildSettings.Quests`).
- [x] 🟡 `ClaimAsync` no-op clarity — **done**: returns `AlreadyParticipated` for an already-reviewed member (vs the old silent `Ok`), with presentation text.
- [x] 🟡 Naming residue from the `Mission→GuildQuest` rename — **done**: `missionId`→`questId` and `mission`→`quest` across `IQuestService`/`QuestService`/`QuestMaintenanceService`; `PostQuestAsync` tuple element `GuildQuest`→`Quest`.
- [x] ❓ **"GuildMaster sets the points."** — **decided (no free-form)**: a manager sets only a **tier** (E–S); bonus POINTS always come from `GuildSettings.Quests.PointsForTier(tier)`. No free-form override — tier→points config map is the single source of truth.
- [x] ❓ Capacity vs repeatable semantics — **decided & simplified**: one completion per member, full stop. Removed `IsRepeatable` and `AllowMultiplePerMember` (premature flags; migration `DropQuestRepeatFlags`). The only knob is **`Capacity`** = how many *distinct* members may complete a guild quest; it closes once `Approved` reaches `Capacity`. Recurring quests (daily-style, spawning a fresh instance) are a **future** feature built as instance-creation, **not** an open-forever flag — out of scope now.

---

## 2A. State-machine flow audit (approve → arbitration → rewards/refunds)

Traced every escrow `Hold` to its matching `Payout`/`Refund` and every non-terminal state to a terminal one.
**Core money path is sound** — Hold→(Payout|Refund) matched on all paths; awards idempotent (participant + ledger
source keys); escrow + status commit together. Gaps:

**Disputes — DONE (fairness policy implemented).** Escrow + adjudication ⇒ impartial judge, burden of proof, guaranteed resolution.
- [x] 🔴 **Recusal** — `ArbitrateAsync(id, reviewerId, pay)` rejects an arbiter who is the owner or taker (system arbiter `0` exempt); board-level manager check added.
- [x] 🔴 **Arbiter recorded** — `reviewerId` threaded through + the resolution audited.
- [x] 🟠 **`DisputeTimeoutHours`** (config; 0 = manual-only) in `QuestSettings`.
- [x] 🟠 **Timeout favors the non-disputing party** — `ResolveStaleDisputesAsync` pays the completer unless the taker raised it (owner-raised / system-raised → pay; taker-raised → refund). Tested both directions.
- [x] 🟠 **`DisputedBy`** tracked on the quest, set in `DisputeAsync`.

**Reject / release — DONE.** Reject is **final per member by design** (revision is the sanctioned retry; reopen is the misclick undo).
- [x] 🟠 **`ReopenQuestRejection`** (manager): `Rejected → Submitted`, only while Open with a free slot.
- [x] 🟠 **Distinct `Released` participant status** — re-claim blocked by `Approved`/`Rejected`, allowed after `Released`; `Release()` now also covers `RevisionRequested`.
- [x] 🟡 **`ClaimAsync` returns `AlreadyParticipated`** for an already-reviewed member (with presentation text).
- [x] 🟡 Personal **intake-reject** stays terminal + refund (accidental ⇒ owner re-posts) — accepted.
- [x] 🟡 **Submission-timeout disabled (0) leaves a past-deadline quest with a pending submission open** — **decided: correct-by-config, documented (no force-resolve)**. A submitted-but-unreviewed quest legitimately outlives its deadline — the worker did their part; the reviewer owes a verdict. `SubmissionTimeoutHours=0` is an explicit "humans always decide" opt-out; force-expiring would discard real work and, for bounties, reopen the pay-vs-refund question the disabled timeout opted out of. Documented on `QuestSettings.SubmissionTimeoutHours` + `ExpireDueQuestsAsync`. Set non-zero to self-resolve.

---

## ✅ 3. Discord surface — COMPLETE (`/quest` subcommand tree)

The flat hyphenated commands were replaced by a single `/quest` group whose subcommands mirror the web/API exactly:
`list` / `show` / `post {guild|player}` / `edit {guild|player}` / `claim` / `submit` / `abandon` / `cancel` /
`confirm` / `dispute` / `finalize` / `arbitrate` / `review {approve|reject|revise|reopen|release}` /
`intake {accept|reject}` / `config {channel|modchannel}`. All write subcommands defer-then-edit and dispatch the
same CQRS command.

- [x] 🔴 **Guild-quest submission reject** — `/quest review reject member:` (manager). Closed; the old intake-only
  `/quest-reject` ambiguity is gone (intake reject is now `/quest intake reject`).
- [x] 🟡 **Submitter selection** — review subcommands take a `member` arg with submitter autocomplete
  (`QuestMemberAutocompleteProvider`); no more generic "missing member" error.
- [x] 🟡 **Naming** — the tree disambiguates: `review reject` (verdict) vs `intake reject` (vetting).

---

## ✅ 4. API surface — v1 done

`ApiQuestEndpoints` (Wolverine.HTTP) under `/api/v1/guilds/{guildId}/quests`, `X-Api-Key` scoped to the guild.

- [x] **Reads** (`read:quests`) — full board parity with the web UI. `GET /quests?tab=active|actionneeded|history&type=guild|player|mine&search=&sort=&desc=&page=&size=` runs the same `GetBoardAsync` the web board uses (filter / search / sort / paging), and `GET /quests/{id}` returns detail + participants, reviewers, dispute. Two filter axes: **tab** (`active` non-terminal / `actionneeded` = manager review queue, empty for non-managers / `history` terminal) × **scope** (`guild` official duties / `player` any bounty / `mine` = bounties the actor posted + anything they claimed, incl. guild duties / all). `actionneeded`, `mine`, `history` and the manager view (submissions-to-review, pending-intake rows) key off the key's **bound actor** + `IsQuestManagerAsync`; an unbound key sees the public active board as a non-manager.
- [x] **Writes** (`write:quests`) — full parity with bot/web: `POST /quests`, `/claim`, `/submit`, `/approve`, `/reject`, `/request-revision`, `/reopen`, `/confirm`, `/dispute`, `/cancel`, `/arbitrate`, `/intake/accept`, `/intake/reject`, `/finalize`, `/edit`. **All 15 §0 commands exposed.** No actor id in any body.
- [x] Endpoints **invoke the §0 commands via `IMessageBus`** — same funnel as bot/web, so handler-level auth applies (no UI to hide buttons).
- [x] **Auth is declarative middleware.** Each endpoint carries `[RequireApiScope("…", requireActor: …)]`; the attribute (a Wolverine `ModifyChainAttribute`) wires `ApiKeyMiddleware` into that endpoint's chain to validate the key/guild/scope (and require a bound actor for writes) *before* the handler, short-circuiting 401/403. The validated `ApiClient` is stashed on the request; handlers read the actor via `HttpContext.ApiActor()`. So there's no repeated auth preamble, and cookie-auth web endpoints (e.g. the audit CSV export) are untouched.
- [x] **Two-layer auth (bound actor).** The **token** carries scopes (`read:quests` / `write:quests` — what it may call); the key is also **bound to a Discord user** (`ApiClient.ActsAsUserId`). Every action runs *as that user* with *their* roles, so a key does at most the **intersection** of its scopes and the actor's permissions. The actor is never request-supplied → no acting-on-behalf-of. One DB read (`ValidateAsync`) yields scopes **and** actor (the old per-request `GuildOwnerIdAsync` is gone). Migration `AddApiClientActsAsUser`.
- [x] **Binding is restricted (privacy).** A key may act only as **the creating admin's own account or a bot/app member** of the guild — never an arbitrary member (no impersonation of a non-consenting user). Bots are now synced with an `IsBot` flag (un-skipped in member sync) so they're bindable, but hidden from human pickers; enforced server-side in `ApiClientService.CreateAsync` via `IsGuildBotAsync`. Migration `AddDiscordUserIsBot`. This binding model is the basis for opening the API to **other user tiers** later. *Deferred:* per-key pseudo-permissions (a key narrower than its actor's full role).

---

## ✅ 5. Web UI surface — mostly done (cockpit pending)

Guild-scoped shell (`GuildLayout`: desktop sidebar + mobile bottom tabs) wrapping board (`Quests.razor`), detail
(`QuestDetail.razor`), post (`QuestPost.razor`), edit (`QuestEdit.razor`). All writes dispatch CQRS commands via the
bus (shared `QuestActionRunner`); button visibility uses `IQuestAuthorizer.Allows`.

- [x] 🟠 **Quest detail view — done.** `QuestDetail.razor`: full participant roster (status rings, the worker's note,
  the reviewer "by X" + per-verdict review note), dispute trail, and **inline manager actions** — per-submitter
  approve/revise/reject with **per-row feedback notes**, reopen, intake accept/reject (+ tier), finalize, arbitrate.
  Reached via "Details →" on each card.
- [x] 🟠 **GuildMaster cockpit — done via the "Action Needed" tab.** The board's `actionneeded` tab *is* the single
  cross-quest manager queue: guild submissions awaiting approval + player quests at intake / final sign-off /
  arbitration, in one list, empty for non-managers. Mirrored on web, API (`tab=actionneeded`), and Discord
  (`/quest list tab:ActionNeeded`). A dedicated dashboard widget (counts/badges) is the only polish left.
- [x] 🟡 **Edit auth — done via the funnel.** `EditQuest` is authorized in the handler (`IQuestAuthorizer.Edit`,
  manager for guild / owner for player); the page surfaces the error and locks edits once anyone is working on it.
- [~] 🟡 **Per-quest history** — the detail page shows the participant/verdict trail + dispute + reviewer; a full
  per-quest **audit-log** surface is still not shown.
- [x] 🟡 **Board flavor — done.** Guild vs player chips, tier **rank badge** (S–E, colored) + tier-colored left edge,
  reward as colored inline text with an escrow lock, claimer **avatar stack** (status rings + hover/tap roster popup).
  Markdown descriptions (Markdig, rendered on card + detail); EasyMDE markdown editor on post/edit.
- [x] 🟡 **Self-participation toggle** — `AllowSelfParticipation` (Config) lets a poster take their own player quest
  (solo/small-guild + testing affordance).

---

## ✅ 6. Tests — comprehensive

All quest tests live under **`Muster.IntegrationTests`** (no Bot dependency → run with the dev app up).
**169 integration + 35 persistence green.**

- [x] **Domain state machine** (`QuestStateMachineTests`) — pure: `Create`/`TransitionTo` guards (terminal lock,
  idempotent no-op) + every `QuestParticipant` verdict transition (valid moves, idempotency, illegal moves throw, notes).
- [x] **CQRS handler flows** (`QuestCommandFlowTests`) — full guild + player lifecycles through the handlers:
  post→claim→submit→approve(mint), reject-stays-open, revise→resubmit→approve, approve-with-note, cancel, edit;
  player confirm-from-escrow, intake accept/reject, dispute→arbitrate-pay, finalize, zero-reward guard.
- [x] 🟠 **Guild-quest reject** — `RejectReopenTests` + flow test (Open after reject, no award, reason recorded).
- [x] 🟠 **Guild request-revision** — flow test (revise→resubmit→approve); **`MaxRevisions` cap** tested
  (`RevisionCap_BlocksBeyondLimit` — the cap is origin-agnostic).
- [x] 🟠 **Guild maintenance sweeps** — `GuildSubmissionTimeout_Approve` (mint + close) + `GuildClaimTimeout`
  (release → re-claimable), alongside the personal sweep cases.
- [x] 🔴 **Non-manager refused at the funnel** — `Approve`/`Reject` by a non-manager + `Arbitrate` recusal
  (`QuestCommandHandlerTests`), the handler being the single enforcement point.
- [x] 🟡 **Origin guard** — `Approve` on a player bounty rejected (`Approve_OnPlayerBounty_IsRejected_ByOriginGuard`).
- [~] 🟡 Optional: one real `IMessageBus.InvokeAsync` end-to-end host test — see *Wishlist → optional polish*.

---

## 7. Notifications / the actual "board"

- [x] ✅ **Live channel board shipped.** `QuestBoardNotificationHandler` (Bot host) consumes `QuestLifecycleNotified` and posts/edits one auto-updating embed card per quest in `QuestSettings.QuestChannelId` (the bot's analogue of the web card — `QuestEmbedRenderer`, tier-coloured). Events are routed over a durable Wolverine **SQL Server queue** (`quest-board`) so a change from any host (web/API/bot/sweep) reaches the bot; only the bot listens. Unset channel → pull-only (`/quest list`).
- [x] ✅ **Message tracking via a generalized table.** New `PostedMessage` (`(EntityType, EntityId) → ChannelId, MessageId`, unique) links a quest to its message so it's edited in place (idempotent: re-read state + edit). General by `EntityType` so musters/events can reuse it. Migration `AddPostedMessages`.
- [x] ✅ **Channel picker.** Web *Admin → Role mapping* `<select>`s (channels fetched live from Discord REST via the bot token — no table) and `/quest config channel` / `/quest config modchannel` (native Discord channel pickers); all call `ConfigCommandService.SetQuestBoardAsync`.
- [x] ✅ **Mod vs public routing.** One card per quest lives in the channel its phase belongs to: mod-only states (`PendingApproval`/`Disputed`/`PendingFinal`) → private `QuestModChannelId`; everything else → public board. Boundary crossings move the card (delete + repost, `PostedMessage` repointed — Discord can't relocate). No mod channel → mod states post nowhere. Dispute/final are temporary detours for an already-public quest → the card **returns to public** on resolution; an intake-rejected (never-public) quest stays hidden, tracked by `PostedMessage.EverPublic`. Pure `TargetChannel` + tests. Migrations `BackfillQuestModChannelId`, `AddPostedMessageEverPublic`.
- [x] ✅ **Card cleanup.** Completed cards linger `QuestSettings.BoardRetentionHours` (default 48; 0 = immediate) after going terminal, then `QuestBoardCleanupScheduler` (bot, 5-min timer) deletes the Discord message + `PostedMessage` row (best-effort, `ExecuteDelete`, multi-node safe). Quest/ledger stay in the DB → web keeps full history. Backfill migration `BackfillBoardRetentionHours`.
- [x] ✅ **Card buttons (interactions).** Cards carry phase/audience components (`QuestComponentBuilder`): public → Claim/Submit (+ owner Confirm/Revise/Dispute, Cancel); mod → tier-select intake + Reject, Approve/Reject/Revise (submitter select-menu for several), arbitrate/finalize Pay/Refund. Clicks run the **same CQRS command as the clicker** (`QuestInteractionModule`/`QuestMenuInteractionModule`) — auth + audit identical to slash/web; ephemeral result, card self-updates via the lifecycle event. custom_id `prefix:guildId:questId[:memberId]`. Offline-validated (routing + builder tests).
- [x] ✅ **Modals (notes).** Submit/Dispute/Reject/Revise open a modal with an optional note (`QuestComponentBuilder.NoteModal`, `QuestModalInteractionModule`) → dispatched with the note. Routing offline-validated.
- [x] ✅ **Per-user DM action cards (on-demand).** Public card = `Claim` + `📨 My actions`; the latter DMs the clicker only the actions that apply to *them* (`QuestComponentBuilder.DmActions` — Submit for claimers, Confirm/Revise/Dispute/Cancel for owners), with an ephemeral fallback if their DMs are closed (`QuestDm.TrySendAsync`). Per-user-correct without fighting Discord's "one component set per shared message". Re-requestable any time.
- [x] ✅ **Auto-push DMs.** `QuestDmPushHandler` (a second bot consumer of `QuestLifecycleNotified`) DMs the moment's target their action card automatically: claimer on `Claimed` (Submit), player-owner on `Submitted` (Confirm/Revise/Dispute), worker on `RevisionRequested` (resubmit). Best-effort (DMs-closed → no-op; the board's *My actions* remains). Claimed now targets the claimer; Submitted already targets the owner (player) / no-one (guild → managers use the mod channel).
- [x] ✅ **Outcome notices (close the loop).** The same handler also pushes a **lightweight, button-less** DM on terminal/outcome moments — `Settled` ("you earned X COIN +pts"), `Rejected` (no reward), `RejectedAtIntake`/`Refunded` (escrow returned), `Released` (slot freed), `Reopened` (back under review) — via `QuestEmbedRenderer.RenderOutcome` (headline + the detail that matters + web link). Previously the worker heard nothing after submitting; now approve/reject/pay/refund all notify. Added a distinct `Rejected` lifecycle moment (the guild-submission verdict reject no longer reuses `RejectedAtIntake`; enum value **appended** so the SQL-queue ordinal serialisation stays stable).
- [x] ✅ **Unclaim / abandon (+ manager force-release).** `ReleaseQuestClaim` command → `QuestService.ReleaseClaimAsync`: a taker abandons their own active claim/submission (`QuestPermission.Abandon`), or a manager force-releases another's (`QuestPermission.ForceRelease`); the slot reopens (status → `Released`, re-claimable), the quest stays Open, a player bounty keeps its escrow held. All surfaces: `/quest abandon` + `/quest review release member:`, public/DM card **Abandon** button, web detail self-abandon + per-participant force-release, API `POST …/abandon` + `POST …/release`. 4 handler tests (self frees slot + Released event, manager force-release, non-manager forbidden, non-participant forbidden).

- [x] ✅ **Deadline reminders (#4).** `QuestReminderScheduler` (bot, 15-min timer) DMs a lightweight "closes soon" nudge (`QuestEmbedRenderer.RenderDeadlineReminder`) before a quest's deadline: each active worker (Claimed/RevisionRequested — they still owe a submission), and a player-bounty **owner** when the quest is about to expire with no taker (extend or cancel to refund). Once per quest — `GuildQuest.DeadlineReminderSentAt` (migration `AddQuestDeadlineReminder`); editing the deadline re-arms it. Window is `QuestSettings.DeadlineReminderHours` (default 24; 0 = off), surfaced in *Admin → Config → automation*. Query `ListQuestsDueForDeadlineReminderAsync`; pure `IsDue` predicate. 2 integration + 6 unit tests.

*(Cross-process delivery still wants a live Aspire smoke test — see Wishlist → verification owed.)*

---

## Wishlist / deferred (post-current)

Captured for later — **none block the feature**; it ships as-is.

**Verification owed (needs the app running — no code work):**
- [ ] 🟡 **Live Discord smoke test** — exercise the interaction/modal/card arc, the on-demand + auto-push DMs, the new outcome notices, and the Abandon button end-to-end (can't be done offline).
- [ ] 🟡 **Cross-process delivery** — confirm a web/API-posted quest renders in the bot's channel under Aspire (the SQL-queue routing is build-verified only).
- [ ] 🟡 **Unit re-run** — stop the app once so the bot-referencing unit suite recompiles + runs (expected green).

**Optional polish (never required):**
- [ ] ⚪ **ASP.NET policy bridge** for the API host — the API already authorizes via the bound-actor funnel; an `IAuthorizationService` policy bridge is a nicety only.
- [ ] ⚪ **One real `IMessageBus.InvokeAsync` end-to-end host test** — the `RecordingMessageBus` stub returns `default`; a Testcontainers-backed test would close the last "emit vs. full pipeline" gap.

**Deferred features:**
- [ ] 🟡 **#3 Submission proof / evidence.** `submit` carries a text note only; reviewers approve blind. Add an
  evidence affordance — a proof **URL** field (Discord modals are text-only, so no native file upload) or a
  "post in a thread" convention. *Wishlist.*
- [x] 🟡 **#4 Deadline reminders via DM** — **DONE** (see §7). Workers/owner get a "closes soon" nudge before the
  deadline.
- [x] 🟡 **#4b Manager pending-review queue — covered by design (no manager DM).** Decided: managers don't get a DM
  digest. The pending queue already lives where managers work — the **mod channel** (one card per pending-intake /
  disputed / awaiting-sign-off quest), with the web **"Action Needed"** tab as the backup UI. Both are push/pull
  already; a per-manager DM would just duplicate them (and needs manager enumeration we don't have). Closed.
- [ ] 🟡 **#5 New-quest announcement ping.** Optional role mention when a quest posts to the public board, to drive
  pickup (board cards currently appear silently). A configurable "quest role". *Wishlist.*
- [ ] 🟢 **#6 Quest stats on the dashboard.** Per-user quests completed / posted / success-rate + history surface
  (data already in the ledger + participants, just unsurfaced). Tracked in **Dashboard.md**. *Deferred.*

---

## Progress

- ✅ **Vertical slice proven** — `Result`/`Result<T>` envelope (Contracts), `GuildActor` (Domain), `IQuestAuthorizer` + `QuestPermission` (full rule set), and `ApproveQuestSubmission` end-to-end: contract → handler (load → authorize → `IQuestService.ApproveAsync`) → bot **and** web invoke via `IMessageBus`. Origin guards on Approve/Reject. 3 handler tests. The remaining 13 actions follow this template.

- ✅ **Dispute fairness + reject recovery shipped** (§2A) — recusal + arbiter id + `DisputeTimeoutHours` with burden-on-disputant default (`DisputedBy`); distinct `Released` status (re-claim allowed) vs final `Rejected` (barred → `AlreadyParticipated`); manager `ReopenRejection`. Discord `/quest-reject-submission` + `/quest-reopen` added (closes the §3 guild-reject gap); arbitrate now passes the arbiter id + has a board-level manager check. Migration `AddDisputeTracking`. 6 new tests (reject-final, reopen, released-reclaim, dispute-timeout owner/taker, + updated existing). *Still on the old direct-service path — these move onto CQRS commands in the §0 migration.*

**§0 — remaining after the slice:**
- [x] **All transition commands migrated** — Approve, Reject, Reopen, Claim, Submit, **Post, Cancel, Confirm, Dispute, Arbitrate, RequestRevision, AcceptIntake, RejectIntake, Finalize**. Bot + web dispatch via the bus (`Dispatch` helper / web local fn), audited by middleware. Guardrails moved into the funnel (claim cap → Claim handler; max-open/validation/currency-resolution → Post handler; date parsing stays adapter-side). Cancel/RequestRevision route by origin in the handler. **Only `EditQuest` left** (the last write).
- [x] `EditQuest` command — done; bot + web QuestEdit save dispatch it. **All 15 write actions are now CQRS commands.** No adapter calls a `QuestBoardService` *write* method anymore.
- [x] **Read split done.** `IQuestReadService` + DTOs (`QuestBoardItem`/`QuestBoardPage`/`QuestEditView`/`QuestListItem`) in `Services/Quests/QuestReadService.cs`. `MusterDbContext` removed from `Quests.razor` (board: tab/type/search/sort/paging + participant + currency/name maps now in the service) and `QuestEdit.razor` (load → `GetForEditAsync`); bot `quest-list` → `GetOpenBoardAsync`. 3 read-service tests. *Web board is build-verified, not browser-verified — worth a manual smoke test.*
- [x] **`QuestBoardService` deleted.** All 15 write methods are now CQRS commands; the web post form (`QuestPost.razor`) and `/timezone` were the last holdouts — migrated (post → `PostQuest` command; `/timezone` → `TimeZoneService` directly). `QuestKind` relocated to the bot. The 3 integration test files now drive the real handlers via a `QuestCommandHarness` test shim. `IQuestService` stays *public* (Wolverine codegen references it); encapsulation is by convention — adapters use commands + `IQuestReadService`.

---

## ✅ §0 COMPLETE

CQRS end-to-end for quests: 15 command contracts (`IGuildCommand`) → thin handlers (load → `IQuestAuthorizer` → `IQuestService`) → bot/web dispatch via `IMessageBus`; audit via middleware; reads via `IQuestReadService` + DTOs (no `MusterDbContext` in adapters); `Result`/`Result<T>` envelope; `Muster.Contracts` is a pure leaf. `QuestBoardService` retired. **208 tests green** (169 integration + 35 persistence + 4 unit). *(Web board + detail browser-verified through iterative UI work.)*
- [x] **DONE — audit in a Wolverine middleware.** `AuditMiddleware.After(Result, Envelope, AuditService)` attached only to `IGuildCommand` chains (filter in `WolverineExtensions`); records on success, action = command type name, actor from the command (`IGuildCommand.ActorId`). Approve's adapter audit removed (bot + web). Verified by a real-bus host test. Surfaced + fixed two prod-relevant Wolverine details: `QuestService` made **public** (codegen can't inline an internal type → service-location, forbidden in v6), and `UseEntityFrameworkCoreTransactions()` now always on so the DbContext is inlinable everywhere.
