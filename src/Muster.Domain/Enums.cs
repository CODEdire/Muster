namespace Muster.Domain.Enums;

public enum TrackingSessionSource
{
    Manual = 0,
    DiscordScheduledEvent = 1,
}

public enum TrackingSessionStatus
{
    Active = 0,
    Closed = 1,
}

public enum ActivityType
{
    Message = 0,
    Voice = 1,
}

/// <summary>A presence transition logged for a session attendee, so the timeline can be reconstructed exactly
/// (vs. inferring it from first-join/last-seen). Emitted by the session reconcile at each state change.</summary>
public enum SessionPresenceKind
{
    /// <summary>Entered the tracked voice channel.</summary>
    Join = 0,

    /// <summary>Started earning (eligible under the anti-AFK guards) — paused → active, or eligible at join.</summary>
    Resume = 1,

    /// <summary>Stopped earning while still in the channel (tripped a guard: muted/deafened/alone).</summary>
    Pause = 2,

    /// <summary>Left the tracked voice channel.</summary>
    Leave = 3,
}

/// <summary>Kind of a synced Discord <see cref="Muster.Domain.Entities.Members.GuildChannel"/>. Governs both
/// the roster (web pickers) and what background tracking the channel supports. Threads/categories/forums
/// are not synced.</summary>
public enum GuildChannelKind
{
    Text = 0,
    Voice = 1,
}

/// <summary>
/// A member's per-guild tracking preference. Governs the always-on "background" plane (active-time stats +
/// background reward) and, at the strongest level, bounded Sessions too. <see cref="Default"/> follows the
/// guild's background policy (<c>GuildSettings.BackgroundTrackingOptIn</c>).
/// </summary>
public enum TrackingChoice
{
    /// <summary>Follow the guild policy: background on unless the guild is opt-in; Sessions always tracked.</summary>
    Default = 0,

    /// <summary>Explicitly opt into all tracking (overrides a guild opt-in default).</summary>
    In = 1,

    /// <summary>Opt out of the background plane (no active-time stats, no background reward); Sessions still tracked.</summary>
    BackgroundOut = 2,

    /// <summary>Opt out of everything — background and Sessions.</summary>
    AllOut = 3,
}

/// <summary>
/// Anti-AFK guards that pause reward accrual. A reward minute counts only when none of the set guards trip.
/// Combinable: e.g. <c>Undeafened | NotAlone</c> pauses a checked-out or alone member but still credits a
/// muted-but-listening one (a phone call). Deafened/alone are the usual signals; muted is opt-in.
/// </summary>
[Flags]
public enum AfkGuards
{
    None = 0,

    /// <summary>Pause while muted (self/server) — can't speak.</summary>
    Unmuted = 1,

    /// <summary>Pause while deafened (self/server) — can't hear, i.e. checked out.</summary>
    Undeafened = 2,

    /// <summary>Pause while fewer than two humans are present in the channel.</summary>
    NotAlone = 4,
}

/// <summary>Helpers for composing/reading <see cref="AfkGuards"/> from the per-guard toggles the UI presents.</summary>
public static class AfkGuardsExtensions
{
    public static AfkGuards Compose(bool unmuted, bool undeafened, bool notAlone)
        => (unmuted ? AfkGuards.Unmuted : 0)
            | (undeafened ? AfkGuards.Undeafened : 0)
            | (notAlone ? AfkGuards.NotAlone : 0);

    public static bool Unmuted(this AfkGuards g) => g.HasFlag(AfkGuards.Unmuted);
    public static bool Undeafened(this AfkGuards g) => g.HasFlag(AfkGuards.Undeafened);
    public static bool NotAlone(this AfkGuards g) => g.HasFlag(AfkGuards.NotAlone);

    /// <summary>Whether a member currently passes the anti-AFK guards: not muted (when the Unmuted guard is on),
    /// not deafened (Undeafened), and not alone (NotAlone, i.e. at least 2 humans present). Shared by the session
    /// and background reconcilers so "is this member eligible to accrue?" is defined in exactly one place.</summary>
    public static bool Allows(this AfkGuards g, bool muted, bool deafened, int humanCount)
        => (!g.Unmuted() || !muted)
            && (!g.Undeafened() || !deafened)
            && (!g.NotAlone() || humanCount >= 2);
}

/// <summary>What kind of window/condition a <see cref="Muster.Domain.Entities.Tracking.RewardMultiplier"/> matches.</summary>
public enum MultiplierKind
{
    /// <summary>A single absolute time window (StartsAt..EndsAt) — a one-off "happy hour" / event boost.</summary>
    OneOff = 0,

    /// <summary>A weekly recurring window (WeekDays + StartTime..EndTime), evaluated in the guild's time zone.</summary>
    Recurring = 1,

    /// <summary>Always-on factor for members holding a given role (VIP / booster). Multiplies the time factor.</summary>
    Role = 2,
}

/// <summary>Which reward planes a multiplier applies to (combinable).</summary>
[Flags]
public enum MultiplierScope
{
    None = 0,
    BackgroundVoice = 1,
    Messages = 2,
    Sessions = 4,
    Quests = 8,
    All = BackgroundVoice | Messages | Sessions | Quests,
}

/// <summary>How overlapping time-window multipliers combine into one factor.</summary>
public enum MultiplierStacking
{
    /// <summary>Use the single highest active factor (predictable, no runaway).</summary>
    HighestWins = 0,

    /// <summary>Multiply all active factors together.</summary>
    Multiplicative = 1,
}

/// <summary>Days of the week a recurring multiplier is active (combinable). Mirrors <see cref="System.DayOfWeek"/> order.</summary>
[Flags]
public enum WeekDays
{
    None = 0,
    Sunday = 1,
    Monday = 2,
    Tuesday = 4,
    Wednesday = 8,
    Thursday = 16,
    Friday = 32,
    Saturday = 64,
    All = Sunday | Monday | Tuesday | Wednesday | Thursday | Friday | Saturday,
}

/// <summary>How a monitored channel is treated for background (always-on) participation.</summary>
public enum TrackedChannelMode
{
    /// <summary>Not monitored (a row exists only to hold disabled config / history).</summary>
    Off = 0,

    /// <summary>Activity is recorded for stats/leaderboards but never awards currency.</summary>
    StatsOnly = 1,

    /// <summary>Activity accrues and awards currency (subject to anti-AFK guards + daily cap).</summary>
    Reward = 2,
}

public enum QuestStatus
{
    Draft = 0,
    Open = 1,
    Closed = 2,
    Cancelled = 3,
    Expired = 4,
    Disputed = 5,

    /// <summary>Created with a future start date; not claimable/takeable until activated at that time.</summary>
    Scheduled = 6,

    /// <summary>Personal quest awaiting a quest manager's intake approval + difficulty tiering before it opens.</summary>
    PendingApproval = 7,

    /// <summary>Personal quest the owner accepted, awaiting a quest manager's final sign-off before payout.</summary>
    PendingFinal = 8,
}

/// <summary>Governs whether a personal quest needs a final manager sign-off before payout, and who decides.</summary>
public enum FinalApprovalMode
{
    /// <summary>Never require final approval — the owner's acceptance pays out directly.</summary>
    Off = 0,

    /// <summary>The owner opts in per-quest when posting.</summary>
    OwnerChoice = 1,

    /// <summary>The intake approver decides when accepting the quest.</summary>
    ApproverChoice = 2,

    /// <summary>Always require a final manager sign-off.</summary>
    Forced = 3,
}

public enum QuestParticipantStatus
{
    Claimed = 0,
    Submitted = 1,
    Approved = 2,
    /// <summary>A reviewer finally rejected the submission — terminal, and bars the member from re-claiming
    /// (a non-repeatable quest is one-shot per member). Reversible only by a manager via reopen.</summary>
    Rejected = 3,
    /// <summary>A reviewer sent the submission back to the same worker to revise and resubmit.</summary>
    RevisionRequested = 4,
    /// <summary>The slot was given up without a verdict — an idle claim timed out, or the quest was
    /// cancelled/expired. Unlike <see cref="Rejected"/>, this does NOT bar the member from re-claiming.</summary>
    Released = 5,
}

public enum EventStatus
{
    /// <summary>Created with a future start time; sign-ups allowed, not yet live.</summary>
    Scheduled = 0,
    /// <summary>Live: members sign up and accrue attendance.</summary>
    Open = 1,
    /// <summary>Closed: attendees marked and awarded.</summary>
    Closed = 2,
    Cancelled = 3,
}

public enum AttendanceStatus
{
    SignedUp = 0,
    Attended = 1,
    NoShow = 2,
}

/// <summary>Outcome applied when a personal quest sits in intake (PendingApproval) past the guild's timeout.</summary>
public enum StaleIntakeAction
{
    /// <summary>Reject at intake and refund the owner.</summary>
    Decline = 0,

    /// <summary>Accept and open the quest with no difficulty tier (no bonus points).</summary>
    Accept = 1,
}

/// <summary>Outcome applied when a submitted quest waits on its reviewer past the guild's timeout.</summary>
public enum StaleSubmissionAction
{
    /// <summary>Settle in the completer's favour (pay/mint, respecting any final-approval requirement).</summary>
    Approve = 0,

    /// <summary>Send the submission back to the worker for revision.</summary>
    Reject = 1,

    /// <summary>Escalate to manager arbitration (personal quests only).</summary>
    Dispute = 2,
}

/// <summary>Outcome applied when a personal quest awaits final sign-off (PendingFinal) past the guild's timeout.</summary>
public enum StaleFinalAction
{
    /// <summary>Pay the completer.</summary>
    Approve = 0,

    /// <summary>Refund the owner.</summary>
    Refund = 1,
}

public enum SeasonStatus
{
    Pending = 0,
    Active = 1,
    Archived = 2,
}

/// <summary>The participation source that produced a ledger entry.</summary>
public enum CurrencyLedgerSource
{
    TrackingSession = 0,
    Quest = 1,
    Muster = 2,
    ManualAward = 3,
    Connector = 4,
    Event = 5,
    Transfer = 6, // member-to-member move (two legs share one source key with :out/:in suffixes)
    Adjustment = 7, // staff balance correction (mint/adjust outside the connector loop)
    Checkpoint = 8, // carry-forward opening balance written when pruning history (sum of the pruned rows)
    Background = 9, // always-on per-channel participation (voice presence / activity outside a bounded session)
}

public enum AppRole
{
    Member = 0,
    GuildAdmin = 1,
    SuperAdmin = 2,
}

/// <summary>Lifecycle of a queued bulk currency adjustment (staff mint/adjust applied to many members async).</summary>
public enum BulkBatchStatus
{
    Pending = 0,   // created, awaiting the background worker
    Running = 1,   // worker is applying member legs
    Completed = 2, // every target applied (or already-applied via idempotent rerun)
    Failed = 3,    // worker stopped on an unrecoverable error
}

/// <summary>Where an audit entry was triggered from — the entry point, not the subsystem touched. Lets the audit
/// console group/filter by surface ("everything the API did", "what the Discord bot did") and gives security a
/// quick answer to "did any human reach in via the UI?". <see cref="Unknown"/> is the legacy/imported fallback.</summary>
public enum AuditOrigin
{
    Unknown = 0,
    UI = 1,
    Bot = 2,
    Api = 3,
    Connector = 4,
    System = 5,
}

/// <summary>The outcome of the audited operation. Today most callers only record on success — <see cref="Denied"/>
/// covers permission/authorization rejections and <see cref="Failed"/> covers unexpected errors. Lets security
/// review surface blocked attempts without trawling logs.</summary>
public enum AuditOutcome
{
    Success = 0,
    Denied = 1,
    Failed = 2,
}

/// <summary>How a currency's balance authority is split with external systems.</summary>
public enum CurrencyMode
{
    /// <summary>Muster owns the balance; external systems read and mint/spend via the API. (default)</summary>
    Internal = 0,

    /// <summary>An external system owns the balance; Muster emits events and keeps a shadow ledger.</summary>
    External = 1,

    /// <summary>Split authority: Muster mints from participation, external owns spend; reconciled via events.</summary>
    Hybrid = 2,
}

/// <summary>How a currency connector authenticates its outbound HTTP calls. OAuth is a future scheme.</summary>
public enum ConnectorAuthScheme
{
    None = 0,
    Basic = 1,   // username + password
    Bearer = 2,  // static token (OAuth client-credentials is a future connector)
    ApiKey = 3,  // a key in a configurable header or query param
}

/// <summary>HMAC algorithm used to sign the request body (orthogonal to <see cref="ConnectorAuthScheme"/>).</summary>
public enum ConnectorSignAlgorithm
{
    None = 0,
    HmacSha256 = 1,
    HmacSha512 = 2,
}

/// <summary>Where an API-key secret is placed on the request.</summary>
public enum ApiKeyLocation
{
    Header = 0,
    Query = 1,
}

/// <summary>How to read a value (e.g. an updated balance) out of a connector action's HTTP response.</summary>
public enum ConnectorResponseFormat
{
    /// <summary>Ignore the response body (just check the status code).</summary>
    None = 0,

    /// <summary>Parse JSON and read the value at the configured dotted path.</summary>
    Json = 1,

    /// <summary>The whole (trimmed) response body is the value.</summary>
    Text = 2,

    /// <summary>Apply a regex (with one capture group) to the body text and parse the captured number.</summary>
    Regex = 3,
}

/// <summary>How a connector action encodes its request body.</summary>
public enum ConnectorBodyFormat
{
    /// <summary>JSON (<c>application/json</c>) — the body template is JSON with quoted tokens.</summary>
    Json = 0,

    /// <summary>Form (<c>application/x-www-form-urlencoded</c>) — the body template is <c>a=$userId&amp;b=$amount</c>.</summary>
    Form = 1,
}

/// <summary>Lifecycle of a reaction muster (check-in post).</summary>
public enum MusterStatus
{
    Open = 0,      // accepting check-ins
    Closed = 1,    // manually closed by staff
    Expired = 2,   // passed its ExpiresAt
    Cancelled = 3, // abandoned (e.g. its Discord message was deleted)
}

/// <summary>How a muster check-in was recorded.</summary>
public enum MusterParticipantSource
{
    Button = 0, // a member clicked the Check-In button
    Admin = 1,  // staff added the member by hand (web/command)
}
