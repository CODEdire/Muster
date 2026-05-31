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

/// <summary>What happens when a standalone muster's active window ends (auto-close / expiry).</summary>
public enum MusterResolveMode
{
    /// <summary>Auto-resolve: the muster closes and pays its roster immediately (the default).</summary>
    Pay = 0,

    /// <summary>Review: the muster soft-closes into a pending state (no check-ins, not paid) so an owner/manager can
    /// vet the roster, then Approve &amp; pay (close) or Discard (cancel). Manual "Close &amp; pay" still finalizes
    /// immediately regardless of this mode.</summary>
    Review = 1,
}

/// <summary>Where an auto-created (on session open) muster posts its card.</summary>
public enum MusterAutoCreateChannel
{
    /// <summary>The guild's configured default muster channel (or, if none, no card is posted).</summary>
    DefaultChannel = 0,

    /// <summary>The session's own (voice) channel — but only when the allow-list permits it; otherwise it falls
    /// back to the default channel.</summary>
    SessionChannel = 1,
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
