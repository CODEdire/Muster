namespace Muster.Domain.Entities.Musters;

/// <summary>
/// A named preset for creating musters — so a creator picks "Tactical Strike Group" or "Contested Zone" instead of
/// dialing in rewards by hand. Its reward/retention values override the guild's global muster defaults. Pre-set
/// rewards are also what makes self-service safe: the Muster Creator role may only post from a template.
/// </summary>
public class MusterTemplate
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Admin-only note describing what the template is for (not shown on the card).</summary>
    public string? Description { get; set; }

    /// <summary>Optional default card heading — used when the author doesn't supply their own title.</summary>
    public string? Title { get; set; }

    /// <summary>Optional default card prompt — used when the author doesn't supply their own. Lets a template-locked
    /// creator post in one step (no text to type).</summary>
    public string? Prompt { get; set; }

    /// <summary>Participation POINTS this template grants (overrides the guild default).</summary>
    public long Points { get; set; }

    /// <summary>Spendable coins this template grants (overrides the guild default). 0 = none.</summary>
    public long Coins { get; set; }

    /// <summary>Currency the coins are minted in (a spendable currency). Null = no coin reward.</summary>
    public Guid? CoinCurrencyId { get; set; }

    /// <summary>How long a terminal card lingers before cleanup (overrides the guild default).</summary>
    public int RetentionHours { get; set; } = 48;

    /// <summary>Optional default hard cap applied to musters from this template (null = uncapped).</summary>
    public int? Capacity { get; set; }

    /// <summary>Minimum check-ins required before this template's musters pay out. null = no minimum; 0 is a valid
    /// (always-met) minimum.</summary>
    public int? MinCheckIns { get; set; }

    /// <summary>Optional default auto-expire window in hours (null = no auto-expire).</summary>
    public int? ExpiryHours { get; set; }

    /// <summary>Retired templates stay for history but aren't offered when creating.</summary>
    public bool Enabled { get; set; } = true;
}
