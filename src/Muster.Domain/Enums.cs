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
}

public enum MissionParticipantStatus
{
    // Quest lifecycle
    Claimed = 0,
    Submitted = 1,
    Approved = 2,
    Rejected = 3,
    // Event-op lifecycle
    SignedUp = 10,
    Attended = 11,
    NoShow = 12,
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
