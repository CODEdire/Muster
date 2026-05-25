# Quest state machine

Muster has one quest board with two **origins**:

- **Guild quest** (`MissionOrigin.Guild`) — funded by the guild, the reward is **minted** on approval. Created
  by a Quest Manager, who sets the difficulty **tier** at creation. May be **repeatable**.
- **Personal quest** (`MissionOrigin.Player`) — funded by the poster, the reward is **escrowed** at post time
  and paid to the completer on settlement. Tiered by an approver at **intake** (members can't self-assign points).

Both share `MissionStatus`, `MissionParticipant`, the escrow ledger, and the difficulty-tier → bonus-POINTS map.
Single taker per quest (for now).

## States (`MissionStatus`)

| State | Meaning | Applies to |
|-------|---------|-----------|
| `Scheduled` | Future start; not claimable until activated | both |
| `PendingApproval` | Personal quest awaiting a manager's intake accept + tiering | personal |
| `Open` | Claimable / in progress | both |
| `PendingFinal` | Owner accepted; awaiting a manager's final sign-off before payout | personal |
| `Disputed` | Owner or taker raised a dispute; awaiting manager arbitration | personal |
| `Closed` | Settled (completer paid / minted) | both |
| `Cancelled` | Refunded (reject / cancel / refund-arbitration) | both |
| `Expired` | Past deadline before completion (refunded if escrowed) | both |
| `Draft` | Unused placeholder | — |

Every status change stamps `Mission.StatusChangedAt`; `Mission.RowVersion` is an optimistic-concurrency token so
two actors can't both settle the same quest.

## Participant lifecycle (`MissionParticipantStatus`)

`Claimed` → `Submitted` → (`Approved` | `RevisionRequested` → `Submitted` … | `Rejected`).

- `ClaimedAt` / `SubmittedAt` / `ReviewedAt` are stamped on each step.
- `Note` is the worker's submission note; `ReviewNote` is the reviewer's reason on revision/reject.
- `RevisionCount` counts send-backs.

## Transitions

### Guild quest
```
(create, tier set) → Open [or Scheduled if future start]
Open + claim                         → participant Claimed
Open + submit (note)                 → participant Submitted
Open + manager approve               → mint reward + tier POINTS; participant Approved; Closed
                                       (if IsRepeatable: clone a fresh Open copy)
Open + manager request-revision      → participant RevisionRequested (worker resubmits)
Open + manager reject                → participant Rejected; quest stays Open for another taker
Open/Scheduled + manager cancel      → Cancelled
past deadline (no submission)        → Expired
```

### Personal quest
```
(post, escrow held) → PendingApproval [intake on] | Open/Scheduled [intake off]
PendingApproval + manager accept(tier) → Open/Scheduled
PendingApproval + manager reject       → refund owner; Cancelled
PendingApproval/Open/Scheduled + owner cancel (no submission) → refund; Cancelled
Open + claim                           → participant Claimed
Open + submit (note)                   → participant Submitted
Open + owner request-revision          → participant RevisionRequested (worker resubmits)
Open + owner confirm:
    RequiresFinalApproval == false     → payout + tier POINTS; Closed
    RequiresFinalApproval == true      → PendingFinal
Open + owner/taker dispute             → Disputed
PendingFinal + manager finalize(pay)   → payout + tier POINTS; Closed
PendingFinal + manager finalize(refund)→ refund owner; Cancelled
Disputed + manager arbitrate(pay)      → payout + tier POINTS; Closed
Disputed + manager arbitrate(refund)   → refund owner; Cancelled
past deadline (no submission)          → refund; Expired
```

## Final-approval policy (`GuildSettings.FinalApprovalMode`)

Whether a personal quest needs a manager's final sign-off before payout, set at post/intake into
`Mission.RequiresFinalApproval`:

- `Off` — never.
- `OwnerChoice` — the owner opts in when posting.
- `ApproverChoice` — the intake approver decides when accepting.
- `Forced` — always.

## Auto-resolve (anti-staleness)

A once-a-minute idempotent sweep (`QuestMaintenanceService`, run by `QuestSweepScheduler`) drives stuck quests
forward using per-guild timeouts (hours; `0` = disabled). Each acts only when `now - <entered-at> > timeout`:

| Setting | Trigger state | Outcome (`*Action`) |
|---------|---------------|---------------------|
| `IntakeTimeoutHours` / `IntakeTimeoutAction` | `PendingApproval` since `StatusChangedAt` | `Decline` (refund) · `Accept` (open, no tier) |
| `ClaimTimeoutHours` | `Open` with a `Claimed` (idle) taker since `ClaimedAt` | release taker → `Open` |
| `SubmissionTimeoutHours` / `SubmissionTimeoutAction` | `Open` with a `Submitted` taker since `SubmittedAt` | `Approve` (settle) · `Reject` (send back for revision) · `Dispute` (personal → arbitration) |
| `FinalApprovalTimeoutHours` / `FinalApprovalTimeoutAction` | `PendingFinal` since `StatusChangedAt` | `Approve` (pay) · `Refund` |

`Approve` on a personal quest respects `RequiresFinalApproval` (so it may move `Open`→`PendingFinal`, which the
final-approval timeout then resolves). All auto-resolves and manual transitions are written to the audit log
(actor `0` = system) and raise lifecycle notifications (`IQuestNotifier`, wired to Discord later).

## Capacity / multi-taker

`Mission.Capacity` is how many completers a **guild** quest rewards (default 1). Up to `Capacity` members may
hold an active slot (`Claimed`/`Submitted`/`RevisionRequested`/`Approved`); each is approved independently and
minted the reward. A non-repeatable guild quest closes once `Approved` reaches `Capacity`; a repeatable one
ignores capacity and stays open. **Personal** quests are single-taker (capacity 1) — multi-taker there needs N×
escrow and per-participant settlement, which is a separate change.

## Editing

`QuestBoardService.EditAsync` patches a quest (blank/null keeps the current value) and is allowed only while the
quest has **no active participant** (nobody `Claimed`/`Submitted`/`RevisionRequested`/`Approved`). Owner edits
personal quests; managers edit guild quests. Reward/tier/capacity are guild-only (a personal reward is escrowed —
cancel and repost to change it). After a submission, reviewers use request-revision rather than editing.

## Completion events & notifications

- **Reward resolution** (`IQuestRewardSink`): every time a completer is paid/minted, a `QuestCompletion` fires so
  an external connector (the CurrencyService / loot system) can resolve rewards beyond Muster's ledger. Default
  impl logs; a connector implementation is registered later.
- **Lifecycle notifications** (`IQuestNotifier`): the services raise targeted events — `Created`/`PendingApproval`
  (managers / board), `Accepted`/`RejectedAtIntake`/`Refunded` (owner), `Claimed` (owner), `Submitted` (reviewer),
  `RevisionRequested` (worker), `AwaitingFinalApproval`/`Disputed` (managers), `Settled` (completer). `Created`
  exists for a formatted board post later. Default impl logs; Discord delivery is wired later. The auto-resolve
  sweep additionally records audit entries (actor `0` = system).

## Guardrails (`GuildSettings`)

- `MaxOpenQuestsPerPoster` — cap on a poster's non-terminal quests (`0` = unlimited).
- `MaxActiveClaimsPerUser` — cap on quests a user has `Claimed`/`Submitted`/`RevisionRequested` (`0` = unlimited).
- `MaxRevisions` — cap on revision round-trips before a reviewer must approve or reject (`0` = unlimited).
- Optimistic concurrency (`Mission.RowVersion`) prevents two actors from both settling a quest.
