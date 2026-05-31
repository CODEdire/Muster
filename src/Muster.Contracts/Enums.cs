namespace Muster.Contracts;

// Shared, dependency-free enums that travel on message contracts (and are reused by the domain). They live
// here so Muster.Contracts references nothing — everything else can depend on it.

/// <summary>Who created a quest and how its reward is funded.</summary>
public enum QuestOrigin
{
    /// <summary>Created by the guild; reward is minted (new currency issued).</summary>
    Guild = 0,
    /// <summary>A player bounty; reward is escrowed from the poster's own balance and transferred on completion.</summary>
    Player = 1,
}

/// <summary>How a tracking session's spendable-coin mint is gated by its linked muster(s) at close. Points are
/// never gated. A round muster is just another linked muster; combine them via this mode.</summary>
public enum SessionCoinGate
{
    /// <summary>No gating — every eligible attendee earns the session coin (the default, and the behavior when no
    /// muster is linked).</summary>
    None = 0,

    /// <summary>Coin minted to an attendee who checked into ANY linked muster (union).</summary>
    Any = 1,

    /// <summary>Coin minted only to an attendee who checked into ALL linked musters (intersection).</summary>
    All = 2,
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

/// <summary>A point in a quest's lifecycle worth telling someone about (Discord DM/post, external API, etc.).</summary>
public enum QuestLifecycleMoment
{
    Created,
    PendingApproval,
    Accepted,
    RejectedAtIntake,
    Claimed,
    Submitted,
    RevisionRequested,
    AwaitingFinalApproval,
    Settled,
    Refunded,
    Disputed,
    Expired,
    Released,
    Reopened,
    Rejected, // appended (not reordered) — the SQL queue serialises this enum by ordinal
}
