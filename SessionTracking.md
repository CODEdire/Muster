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

From NetCord `VoiceState`: skip self-muted/deafened and server-muted/deafened (`RequireUnmuted`),
skip alone-in-channel (`RequireNotAlone`, occupancy from the gateway roster), and cap
per-member-per-channel-per-day points (`DailyCapPoints`).

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
- **P3 — Session COIN minting.** Admin-picked `SessionCoinCurrencyCode` + `MinutesPerCoin`; on session
  close, mint that spendable currency to each attendee = floor(eligibleMinutes / MinutesPerCoin), via
  `CurrencyLedgerSource.TrackingSession` (idempotent `session:{id}:user:{id}:coin`). POINTS award unchanged.
  Background mints no COIN.
- **P4 — Admin participation reports + CSV; participation leaderboards.**
- **P5 — Guarded Sessions.** Unify Session accrual onto the snapshot/occupancy reconcile engine so
  `RequireUnmuted`/`RequireNotAlone` apply to Sessions per `ApplyAfkGuardsToSessions`.
- **P6 — Live ops board + member self-view.**
- **P7 — Hardening.** Minimum segment threshold (ignore sub-minute drive-bys to cut noise); other
  robustness/cleanup as it surfaces.
- **P8 — Multipliers.** Time-bounded reward multipliers: event windows (2× during a scheduled event /
  admin "happy hour") **and recurring peak-time schedules** (e.g. ×1.5 on weeknights 7–10pm in the
  guild's time zone). Applies to POINTS (and Session COIN). Stacking rules TBD.
- **P9 — Tracking transparency notice.** How to inform members that participation is tracked (first-touch
  and/or Session-start) **without broadcasting to every member**. Deferred deliberately — needs design
  (e.g. ephemeral on first interaction, a pinned info message, or onboarding text — not a mass DM/ping).

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
