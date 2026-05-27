# Session Tracking

Tracks **overall participation** across three modes and turns it into stats and (optionally)
currency. Aligns with the existing GuildEvents and Currency architecture: domain entity +
`*Service` (logic) + `*CommandService` (platform-independent) + thin bot module, idempotent ledger
awards via `ICurrencyService`, durable Wolverine events for Discord side-effects.

## Goals

Track participation in:

1. **Scheduled Events** — Discord scheduled events (auto bind/unbind). *Built.*
2. **Ad-Hoc Sessions** — manual admin-opened ops (`/track-start` / `/track-stop`). *Built.*
3. **Background activity** — always-on, per-channel voice presence + messages, with anti-AFK
   guards. Can mint currency. *New.*

Admins pick which channels are monitored and how each is rewarded.

## Two planes

The feature has **two reward planes** plus a **stats lane** that runs underneath both:

| Lane | Container | Trigger | Guards | Pays |
|------|-----------|---------|--------|------|
| **Active time** (stats) | `DailyActivityRollup.VoiceMinutes` (+ season counter) | any presence in a tracked voice channel, anytime | none — overlaps everything | never |
| **A — Sessions** (reward) | `TrackingSession` (bounded window) | scheduled event Active, or `/track-start` | global toggle, default ON | POINTS per minute + **COIN on close, by minutes** |
| **B — Background** (reward) | continuous accrual per `(guild, user, channel)` | monitored voice/text activity, anytime | per-channel anti-AFK | POINTS per minute (no COIN) |

### Active time vs reward time

**Active time** is the raw "how present is this member" denominator: it accrues for *all* presence in
a tracked voice channel, ignores anti-AFK guards, **overlaps freely** with both reward planes, and never
pays. It powers stats, leaderboards, and live "who's around" views. **Reward time** (Sessions +
Background) is always a guarded subset of active time, and is mutually exclusive between the two planes
via the overlap rule below.

### Overlap rule — Session wins (reward only)

When a Session is active on a channel that is also background-monitored, **background reward accrual is
suppressed there** (the time is attributed to the Session — no double-pay). Active-time stats are
unaffected and keep accruing for everyone. Background reward resumes when the Session closes.

## Per-channel configuration

Channel rules are a relational table (`TrackedChannel`), not a JSON blob — they carry per-channel
rates, guards, and caps, and need to scale to many channels and back a web grid. The existing dead
`GuildSettings.TrackedChannelIds` field is retired.

```
TrackedChannel
  GuildId       ulong
  ChannelId     ulong
  Kind          TrackedChannelKind   // Voice | Text
  Mode          TrackedChannelMode   // Off | StatsOnly | Reward
  PointsPerMinute    int             // background POINTS rate (overrides global default)
  PointsPerMessage   int             // text reward rate (P2)
  DailyCapPoints     int             // anti-farm cap per member/channel/day (0 = uncapped)
  RequireUnmuted     bool            // anti-AFK: skip self/server muted or deafened
  RequireNotAlone    bool            // anti-AFK: skip when alone in the channel
```

Edited via `/track-voice|track-text|track-untrack|track-channels` and (P4) a web grid.

## Background accrual & awarding

Always-on voice never "closes", so it cannot award-on-close like a Session. Instead:

- **Accrue**: voice state changes open/close a presence segment per `(guild, user, channel)`
  (`BackgroundVoicePresence`). Guards are applied at accrual time.
- **Flush**: a leader-only hosted service (`BackgroundFlushScheduler`, copy of `QuestSweepScheduler`)
  ticks ~every 5 min and mints eligible accrued minutes for still-present members; a member leaving
  also triggers a flush. Crash-safe and near real-time.
- **Idempotency**: source key `bg:{channelId}:{userId}:{minuteBucket}` against the ledger's unique
  `(SourceType, SourceId)` index. `CurrencyLedgerSource.Background`.
- **Daily cap**: before minting, sum the day's `Background` ledger for `(user, channel)` and clamp.

Minting publishes `CurrencyMovementRecorded` (the existing money-moved seam), so DM receipts work for
free.

## Anti-AFK guards

From NetCord `VoiceState`, three independent, configurable guards:
- **`RequireUnmuted`** — skip muted (self/server) members. They *can't speak* but may still be present and
  listening (e.g. on a phone call), so this is **off by default**.
- **`RequireUndeafened`** — skip deafened (self/server) members. They *can't hear* = checked out, so this is
  the primary AFK signal and **on by default** (with guards).
- **`RequireNotAlone`** — skip when fewer than two humans are in the channel (occupancy from the gateway roster).

Plus the per-member-per-day cap (`DailyCapPoints`). Each guard is set per background channel (`/track-voice`,
web) and per session (`/track-start`, seeded from `ApplyAfkGuardsToSessions`). Existing rows from before the
split keep their prior combined behavior (migration backfills `RequireUndeafened = RequireUnmuted`).

**Guards now apply to Sessions too** (a planned change, P5): a Session can be opened with the same
pause-while-muted / pause-while-alone rules so AFK sitters in a raid channel don't bank reward time.
This pushes Session accrual onto the same snapshot-driven, occupancy-aware reconcile engine the
Background plane already uses (today's session accrual is a dumb per-user voice-delta with no mute or
occupancy awareness). Active-time stats always ignore guards regardless.

## Currency conversion & global rates

Time converts into two currencies:

- **POINTS** (seasonal, leaderboard "score") — `PointsPerVoiceMinute` (exists). Awarded by both reward
  planes. Per-channel `TrackedChannel.PointsPerMinute` **overrides** the global default for Background;
  Sessions use the global default.
- **A spendable currency (COIN-like)** — minted **only by Sessions, on close, proportional to each
  attendee's eligible minutes**. The admin picks *which* of their spendable currencies time mints into
  (`SessionCoinCurrencyCode`); no currency is assumed/auto-created. Rate is **minutes-per-coin** (coarse,
  since the spendable currency is more valuable): e.g. 30 → 1 coin per 30 eligible minutes, integer-floored.

```
GuildSettings (additions)
  PointsPerVoiceMinute     int     // exists — POINTS per eligible minute (global default)
  SessionCoinCurrencyCode  string? // NEW — which spendable currency Sessions mint on close (null = none)
  MinutesPerCoin           int     // NEW — eligible minutes per 1 coin on session close (0 = no COIN)
  ApplyAfkGuardsToSessions bool    // NEW — sessions honor RequireUnmuted/RequireNotAlone (default ON)
```

Background mints **no** COIN — it stays POINTS-only, so the spendable economy is earned through
deliberate, guarded events rather than passive idling.

## Seasons & time attribution

POINTS already season-stamp at the ledger (seasonal currency). For **time stats** (active time, voice
hours) we attribute minutes to the season that was active when they accrued — accrue-as-you-go into a
per-`(guild, user, season)` counter, so no boundary recalculation is ever needed and a session
spanning a season rollover is split exactly at the boundary by construction. Day-level rollups
(`DailyActivityRollup`) remain for per-day/per-channel stats; season totals come from the live counter
(authoritative) rather than re-summing days across a fuzzy boundary.

## Architecture alignment

- **Awarding**: reuse `ICurrencyService.AwardPointsAsync`; add `CurrencyLedgerSource.Background = 9`
  (keep `TrackingSession = 0` for Plane A).
- **Events**: minting already fires `CurrencyMovementRecorded`. Add `TrackingBoardNotified` (durable
  `tracking-board` queue → bot) later for the live ops board, mirroring `QuestLifecycleNotified`.
- **Participant gate**: `GuildAuthorizationService.IsParticipantAsync` is checked on every award.
- **Sweep**: leader-only via Wolverine cluster election, same as `QuestSweepScheduler`.

## Value surfaces

- **Admin participation reports** (web): member × source (Event / Op / Background) × season, CSV.
- **Live ops view**: who's in voice now (open segments) + active sessions; Discord board message.
- **Member self-view**: `/me` extension — voice hours, messages, rank, streak.
- **Leaderboards**: voice-hours + activity, separate from currency balance.

## Phasing

- **P1 — Channel config + background voice reward. ✅ Done.** `TrackedChannel` +
  `BackgroundVoicePresence` entities, `TrackedChannelMode`/`Kind` enums, `CurrencyLedgerSource.Background`,
  `BackgroundTrackingService` (snapshot-driven reconcile: eligibility, Session-wins overlap, flush+award,
  daily cap, participant gate), `TrackedChannelCommandService`, `BackgroundFlushScheduler` (bot, ~5 min),
  `/track-voice|track-text|track-untrack|track-channels`, voice-handler wiring sourcing occupancy from the
  gateway cache, migration `AddSessionTrackingChannels`, `TrackedChannelIds` retired. Anti-AFK guards:
  `RequireUnmuted` (self/server mute+deaf) and `RequireNotAlone` (≥2 humans).
  *Fast-follow (done):* over-award protection — a per-flush clamp (`MaxFlushMinutes`, credits at most one
  sweep window so a gateway reconnect with a stale watermark can't pay the gap) + a startup void of open
  segments (`VoidOpenSegmentsAsync`, called by `BackgroundFlushScheduler` so a process restart doesn't
  credit downtime).
  *Deferred from P1:* message-stats channel scoping moved to P2 (kept global to preserve current behavior).
- **P2 — Active-time stats + season attribution + opt-out. ✅ Done.** Active-time accrual added to the
  reconcile engine (`BackgroundVoicePresence.ActiveOpenSegmentStart/ActiveCarrySeconds`): unguarded, overlaps
  Sessions, rolls into `DailyActivityRollup.VoiceMinutes` + a per-`(guild, user, season)` `SeasonParticipation`
  counter (accrue-as-you-go = exact season split). Reconcile now visits all Mode≠Off voice channels (active for
  all, reward for Reward-mode). **Messages:** scoped to tracked text channels; Reward-mode mints
  `PointsPerMessage`. **Privacy:** 4-state member preference `TrackingChoice` (Default/In/BackgroundOut/AllOut)
  on `GuildMember` via `/track-privacy`, guild `BackgroundTrackingOptIn` toggle via
  `/config-background-tracking`, resolved by `TrackingConsentResolver` and enforced before every active/background
  write; `AllOut` also skips Session reward. Migration `P2ActiveTimeSeasonsOptOut`. Over-award clamp + startup
  void extended to the active lane.
  *Deferred:* message-reward anti-spam throttle → P7; transparency notice → P9.
- **P3 — Session COIN minting. ✅ Done.** Guild settings `SessionCoinCurrencyCode` + `MinutesPerCoin`
  (`/config-session-coin`, currency autocomplete, validates spendable). On `CloseAsync`, each rewarded
  attendee is also minted `floor(eligibleMinutes / MinutesPerCoin)` of that currency via
  `CurrencyLedgerSource.TrackingSession` with idempotent key `session:{id}:user:{id}:coin` (distinct from the
  POINTS key). Honors the same participant + `AllOut` opt-out gates. POINTS award unchanged; Background mints
  no COIN. Settings are JSON (empty migration `P3SessionCoin` keeps the model snapshot in sync).
- **P4 — Participation reports + leaderboards. ✅ Done.** `ParticipationReadService`: voice-time leaderboard
  (active season via `SeasonParticipation`, else all-time from rollups) + a per-member report (voice minutes,
  message counts, points by reward source: Session/Background/Event/Quest/Muster) over a date range. Surfaces:
  `/voice-leaderboard` Discord command; admin CSV export at `GET /guilds/{guildId}/participation/export.csv`
  (cookie + admin auth, mirrors the audit export). Read-only — no migration.
- **P5 — Guarded Sessions. ✅ Done.** Session accrual moved onto a snapshot/occupancy-driven engine
  (`TrackingSessionService.ReconcileSessionsAsync`, replacing the per-user `ProcessVoiceStateAsync`). When
  `GuildSettings.ApplyAfkGuardsToSessions` is on (default), session reward time pauses while a member is
  muted/deafened or alone in the channel; off counts raw presence. `VoiceAttendance.CarrySeconds` adds
  sub-minute precision; `VoidOpenAttendanceAsync` voids stale segments on startup. Handler + flush scheduler
  drive both planes from one roster snapshot. Migration `P5GuardedSessions` (CarrySeconds column; the toggle
  is JSON). No per-flush clamp on sessions (admin-bounded; startup-void covers restarts) so an explicit
  close/leave settles true elapsed.
- **P6 — Live ops + member self-view. ✅ Done.** `ParticipationReadService` gains `ActiveSessionsAsync`
  (live ops: attendees + present-now), `RecentSessionsAsync` (history), `MemberVoiceStatsAsync` (season/all-time
  minutes + season rank). Combined admin page `Sessions.razor` at `/guilds/{id}/sessions` (active ops + voice
  leaderboard + CSV export + history), wired into the guild nav. Member voice panel added to `MyProfile.razor`.
  Server-rendered (reload to refresh); the live read is isolated in `ActiveSessionsAsync` as the single seam a
  future **SSE/SignalR** feed replaces to push updates — no markup change needed then.
  *Future (noted, not in scope):* let members claim event roles / ship positions in the live-ops view.
- **P6.1 — Session UX + admin web polish. ✅ Done.** Sessions now have a **name** and **per-session
  anti-AFK guards** (`TrackingSession.Name/RequireUnmuted/RequireNotAlone`): `/track-start` takes a name +
  `skip-muted` (default on) / `skip-alone` (default **off**, so a solo admin op tracks the opener);
  `ApplyAfkGuardsToSessions` seeds the defaults and `ReconcileSessionsAsync` reads per-session flags.
  `/track-stop` uses an **active-only autocomplete** (pick by name, no GUID). Scheduled-event sessions take
  the event name. Web: every admin page now renders in the guild shell (`@layout GuildLayout`); the admin hub
  gains **Sessions** + **Tracking** cards; new **Tracking settings** page (`/guilds/{id}/tracking`) edits
  background opt-in, session guards, session-coin currency + minutes-per-coin, and the monitored-channel list.
  Migration `SessionNameAndGuards`.
- **P6.2 — Sessions as an operational view. ✅ Done.** Design goal: admin hub trends toward *config only*;
  operational views live on the nav rail. Sessions moved off the admin hub onto the **nav rail** (member-visible).
  `Sessions.razor` rebased to `GuildMemberComponentBase` with an **access gradient**: members see **Active** +
  **Leaderboard** (read-only); **History** + CSV export are staff (admin/officer) only. SSR query-param
  **datagrid** (tabs + search + sortable headers + paging), mirroring the audit console. **Drill-in**
  `SessionDetail.razor` (`/sessions/{id}`) shows the full attendance roster (member, joined, minutes,
  present-now) for active or closed sessions — open to any member. An **opt-out CTA** shows on the live tab when
  the viewer is currently tracked, linking to a new **web privacy control** on `MyProfile` (4-state
  `TrackingChoice` via `TrackingPreferenceCommandService`). Read layer gained `PagedResult<T>`,
  `ActiveSessionsPageAsync`/`RecentSessionsPageAsync` (search/sort/page), and `SessionDetailAsync`.
  *Future:* the Active tab stays the single SSE/SignalR seam.
- **P7 — Hardening. ✅ Done (a/b/c).**
  - *P7a — consent + lifecycle:* session reconcile excludes `AllOut` members entirely (no row); opting out
    mid-session evicts in-progress rows (AllOut → active attendance + background presence; BackgroundOut →
    background presence); `MaxSessionHours` auto-close sweep so a never-stopped session can't accrue forever.
  - *P7b — message anti-spam:* per-channel `MessagesPerPoint` + `MessageCooldownSeconds` + `MessageDailyCapPoints`,
    tracked in `MessageRewardState`; closes the spam-to-mint hole. Configurable via `/track-text` + web.
  - *P7c — activity pruning:* `ActivityRetentionDays` + daily `ActivityPruneScheduler` deletes raw
    `ActivityRecord` rows beyond the window (rollups kept).
  - *P7d — minimum-segment threshold:* `MinTrackedSeconds` (0 = off) drops drive-by attendees — a member who
    leaves a session having accrued less than the minimum is removed from its roster (at the leave reconcile and
    again at close), so they don't inflate the attendee count or get rewarded. Scoped to sessions; the background
    plane is already noise-free (sub-minute presence floors to 0 minutes and writes no rollup).
  - *Deferred:* stream/video mute-exemption — nice-to-have, not scheduled.
- **P7.5 — Scale & robustness. ✅ Done (with reasoned scope).**
  - *Thundering-herd + concurrency:* `GuildReconcileCoordinator` (bot singleton) debounces voice-event bursts
    into one reconcile per guild (~2s window) and serializes per guild via a keyed semaphore — so the voice
    handler, the 5-min sweep, and session-start scans never race the minute bookkeeping (the ledger was already
    idempotent; the counters weren't). Handler now just `Schedule`s; sweep + scans go through `ReconcileNowAsync`.
    The sweep is the backstop if churn ever starves a debounce.
  - *Session gap clamp:* a generous 12h `MaxFlushMinutes` bound on attendance flushes — never clips a normal
    session (the sweep keeps real flushes small) but caps an absurd stale watermark a gateway gap could leave,
    on top of startup-void (restart) and `MaxSessionHours` (total).
  - *Deferred, with reasoning:* **config cache** — the debounce collapses the reconcile storm that justified it,
    and untracked-channel messages already short-circuit in one indexed query, so caching EF entities in the hot
    path is net-negative (staleness + churn) for the remaining gain. **Leader-gated sweeps** — the codebase's
    pattern is idempotent/multi-node-safe (per `QuestSweepScheduler`), not leader election; gating adds risk for
    no benefit on a single-node deploy. Revisit both if real traffic/scale-out demands it.
- **P8 — Multipliers & bonuses.** Time-bounded reward multipliers: event windows (2× during a scheduled
  event / admin "happy hour") **and recurring peak-time schedules** (e.g. ×1.5 on weeknights 7–10pm in the
  guild's time zone). Applies to POINTS (and Session COIN). Stacking rules TBD. **Plus configurable
  presence bonuses** — a flat bonus for being there at the **start** and/or **end** of a session (rewards
  punctuality + staying to the finish); per-guild amounts, awarded on session close to members whose
  attendance window covered the open/close moments.
- **P9 — Tracking transparency notice.** How to inform members that participation is tracked (first-touch
  and/or Session-start) **without broadcasting to every member**. Deferred deliberately — needs design
  (e.g. ephemeral on first interaction, a pinned info message, or onboarding text — not a mass DM/ping).

## Sessions UX round 2 (post-P7)

- **Round A — channel names, Background tab, detail polish. ✅ Done.** Channels show **by name** everywhere
  (stored `TrackingSession.VoiceChannelName` / `TrackedChannel.ChannelName`, captured at creation and refreshed
  from the gateway cache each reconcile by `GuildReconcileCoordinator`; falls back to `#id`). New **Background**
  tab on the Sessions page (staff, shown when the guild has monitored voice channels): channels currently
  carrying tracked presence + who's in them (`BackgroundNowAsync`). `SessionDetail` reworked: a stat-grid header
  (type, channel, started, status), a **Rules** panel showing the session's anti-AFK guards (reference), and a
  richer member **status** — Active (accruing) / Present-not-earning (muted/alone) / Left (with last-seen time),
  derived from a new `VoiceAttendance.LastSeenAt` updated for every present member each reconcile. Migration
  `ChannelNamesAndLastSeen`.
- **Round B — Me dashboard + per-session opt-out + personal history. ✅ Done.** Wallet page moved to `/wallet`;
  new **Me dashboard** at `/me` (a reverse of the admin Sessions view): a stat-grid summary (points balance,
  season/all-time voice + rank, link to the wallet), the **active sessions you're in** (`MemberActiveSessionsAsync`,
  your minutes + status), and **your recent sessions** (`MemberSessionHistoryAsync`, your own perspective). A
  **one-time per-session opt-out** (`SessionOptOut` + `TrackingSessionService.OptOutMemberAsync`) removes you from
  that single session for its remainder — the reconcile excludes opted-out users (alongside `AllOut`) and your
  attendance row is deleted — distinct from the standing `TrackingChoice` preference (still on the wallet page).
  Nav: sidebar gains **Me** (`/me`) + **Wallet** (`/wallet`); the bottom "You" tab points at the dashboard.
  Migration `SessionOptOut`.

## Privacy & consent

Tracking voice/activity on a public bot needs a clear consent model.

**Member opt-out — three levels** (per-guild preference, recommended on `GuildMember` so a member can
choose differently per server; toggled via `/track-optout <scope>` + web):

| Level | Background plane (always-on stats + reward) | Sessions (events / `/track-start`) |
|-------|---------------------------------------------|------------------------------------|
| **None** (default) | tracked | tracked |
| **Background** | **excluded** (no active-time stats, no background reward) | tracked |
| **All** | **excluded** | **excluded** (no session attendance/reward/stats) |

Sessions are deliberate, admin-run participation, so only the **All** level removes them; **Background**
opts out of just the passive always-on monitoring.

**Guild default — background only.** The guild toggle governs the **background plane** for members who
haven't set their own preference: **opt-out** (background on by default, members can leave) vs **opt-in**
(background off until the member opts in). It does **not** disable Session tracking (an admin running an
op is its own consent). A member's explicit preference always overrides the guild default.

**One-time, not per-session.** The preference is a single persistent setting, never a per-session prompt
(per-session nagging would tank participation).

**Transparency notice — deferred to P9.** A first-touch / Session-start notice is the right idea but
risks **broadcasting to every member in the server**; how (and whether) to surface it without spamming
needs more thought, so it's split out to its own phase rather than bundled with the opt-out plumbing.

*Slots in alongside **P2** (when silent always-on tracking truly begins): member preference + guild
background toggle land there; the notice/announcement is **P9**.*

## Ideas (outside the box — not yet decided)

Brainstorm to react to; none committed:

- **Streak / consistency bonus** — reward showing up N days running; rewards habit over marathon sitting.
- **Diminishing returns / daily fatigue** — rate decays after X hours/day so it's not a no-life-farm; healthier than a hard cap.
- **Event/peak multipliers** — → scheduled as **P8**.
- **Role multipliers** — VIP/booster roles earn at a higher rate.
- **Minimum segment threshold** — → scheduled as **P7 (Hardening)**.
- **Per-channel currency targeting** — some channels mint COIN, some POINTS, some both.
- **Discord AFK channel / `Suppressed` (stage audience) auto-exclusion** — natural idle signal beyond mute.
- **Restart/gateway-gap safety** — ✅ shipped as a P1 fast-follow (per-flush clamp + startup void).
- **Member privacy / opt-out** — → promoted to the **Privacy & consent** section above.
- **Speaking detection** — *not feasible* without a voice-receive bot; note the limitation so "active" ≠ "talking".
