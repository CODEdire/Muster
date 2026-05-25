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

public enum MissionType
{
    /// <summary>Claimable task: claim -> submit -> officer approve. No time tracking required.</summary>
    Quest = 0,
    /// <summary>Scheduled operation with RSVP and attendance.</summary>
    EventOp = 1,
}

/// <summary>Who created a quest and how its reward is funded.</summary>
public enum MissionOrigin
{
    /// <summary>Created by the guild; reward is minted (new currency issued).</summary>
    Guild = 0,
    /// <summary>A player bounty; reward is escrowed from the poster's own balance and transferred on completion.</summary>
    Player = 1,
}

public enum MissionStatus
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

/// <summary>Difficulty tier for a guild quest, driving the bonus POINTS reward via guild config. S is hardest.</summary>
public enum QuestTier
{
    None = 0,
    E = 1,
    D = 2,
    C = 3,
    B = 4,
    A = 5,
    S = 6,
}

public enum MissionParticipantStatus
{
    // Quest lifecycle
    Claimed = 0,
    Submitted = 1,
    Approved = 2,
    Rejected = 3,
    /// <summary>A reviewer sent the submission back to the same worker to revise and resubmit.</summary>
    RevisionRequested = 4,
    // Event-op lifecycle
    SignedUp = 10,
    Attended = 11,
    NoShow = 12,
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
    Mission = 1,
    Muster = 2,
    ManualAward = 3,
    Connector = 4,
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
