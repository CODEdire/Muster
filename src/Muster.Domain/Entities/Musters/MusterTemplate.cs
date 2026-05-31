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
    public string? Description { get; set; }

    /// <summary>Optional glyph shown on the card for this template's musters.</summary>
    public string? Emoji { get; set; }

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

    /// <summary>Optional default auto-expire window in hours (null = no auto-expire).</summary>
    public int? ExpiryHours { get; set; }

    /// <summary>Retired templates stay for history but aren't offered when creating.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Display order in the template picker (ascending).</summary>
    public int SortOrder { get; set; }
}
