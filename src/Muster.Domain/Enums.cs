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
public enum LedgerSourceType
{
    TrackingSession = 0,
    Quest = 1,
    Muster = 2,
    ManualAward = 3,
    Connector = 4,
    Event = 5,
}

public enum AppRole
{
    Member = 0,
    GuildAdmin = 1,
    SuperAdmin = 2,
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
