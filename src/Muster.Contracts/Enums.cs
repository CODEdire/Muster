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
