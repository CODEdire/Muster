using Muster.Domain.Enums;

namespace Muster.Domain.Entities.Guilds;

/// <summary>
/// Per-guild participation-tracking configuration — its own table (moved out of the <c>GuildSettings</c> JSON blob,
/// following the <see cref="GuildMusterSettings"/> table-per-feature pattern). Flat scalar columns. This same type
/// is bound from configuration via <c>IOptions</c> to provide the platform defaults used to seed a guild's row the
/// first time it's written, and to fill read-misses for guilds with no row yet.
/// </summary>
public class GuildTrackingSettings
{
    /// <summary>Owning guild — primary key + FK (1:1 with <c>Guild</c>). 0 on the IOptions defaults template.</summary>
    public ulong GuildId { get; set; }

    // --- Background plane + privacy ---

    /// <summary>Background-plane consent default for members who haven't set their own preference. <c>false</c>
    /// (default) = opt-out: background tracking is on, members may leave. <c>true</c> = opt-in. Does not affect Sessions.</summary>
    public bool BackgroundTrackingOptIn { get; set; }

    // --- Session payout ---

    /// <summary>Spendable currency code a Session mints on close (in addition to POINTS), or null to mint none.</summary>
    public string? SessionCoinCurrencyCode { get; set; }

    /// <summary>Eligible voice minutes per 1 unit of <see cref="SessionCoinCurrencyCode"/> on session close (floored).
    /// 0 (default) disables session coin minting.</summary>
    public int MinutesPerCoin { get; set; }

    /// <summary>Points awarded per minute of voice attendance when a tracking session closes.</summary>
    public int PointsPerVoiceMinute { get; set; } = 1;

    // --- Default anti-AFK guards per tracking lane (AfkGuards flags: Unmuted|Undeafened|NotAlone) ---
    // Guild baseline; a monitored channel may override any lane (GuildChannel.*Guards) and a manual session may
    // override at open. Default for every lane: pause while deafened or alone, allow muted.

    public AfkGuards DefaultBackgroundGuards { get; set; } = AfkGuards.Undeafened | AfkGuards.NotAlone;
    public AfkGuards DefaultSessionGuards { get; set; } = AfkGuards.Undeafened | AfkGuards.NotAlone;
    public AfkGuards DefaultEventGuards { get; set; } = AfkGuards.Undeafened | AfkGuards.NotAlone;

    // --- Session controls ---

    // null on a guild row = inherit the platform server default (GuildDefaults:Tracking). The resolution helpers
    // on the read side coalesce null → server default → the documented fallback.

    /// <summary>Auto-close an active session once it has run this many hours (safety net). 0 = never auto-close;
    /// null = inherit the server default.</summary>
    public int? MaxSessionHours { get; set; }

    /// <summary>How many days of raw <c>ActivityRecord</c> rows to keep before the prune sweep deletes them (rollups
    /// always kept). 0 = keep raw rows forever; null = inherit the server default.</summary>
    public int? ActivityRetentionDays { get; set; }

    /// <summary>Minimum seconds a member must accrue in a session to be kept on its roster (drops drive-by join/leaves).
    /// 0 = keep everyone; null = inherit the server default.</summary>
    public int? MinTrackedSeconds { get; set; }

    // --- Reward multipliers & presence bonuses ---

    /// <summary>How overlapping time-window multipliers combine: highest active factor (default) or multiply together.</summary>
    public MultiplierStacking MultiplierStacking { get; set; } = MultiplierStacking.HighestWins;

    /// <summary>Upper clamp on the final effective multiplier (after stacking + role factor). 0 = no cap;
    /// null = inherit the server default.</summary>
    public decimal? MultiplierCap { get; set; }

    /// <summary>Flat POINTS bonus awarded on session close to members present at the start. 0 (default) = no start bonus.</summary>
    public int SessionStartBonus { get; set; }

    /// <summary>Flat POINTS bonus awarded on session close to members present at the end. 0 (default) = no end bonus.</summary>
    public int SessionEndBonus { get; set; }

    /// <summary>A member qualifies for the start bonus if they joined within this many minutes of the session start.</summary>
    public int StartBonusWindowMinutes { get; set; } = 5;

    /// <summary>A member qualifies for the end bonus if they were still present within this many minutes of the session end.</summary>
    public int EndBonusWindowMinutes { get; set; } = 5;

    /// <summary>When true, the active reward multiplier also scales the start/end presence bonuses. Default false = flat.</summary>
    public bool MultiplyPresenceBonuses { get; set; }

    // --- Resolution: a guild row's null nullable-field falls back to the platform server default, then a hard
    // fallback. Pass the IOptions defaults template as <paramref name="serverDefault"/>; null skips that tier. ---

    public const int FallbackMaxSessionHours = 24;
    public const int FallbackActivityRetentionDays = 0;
    public const int FallbackMinTrackedSeconds = 0;
    public const decimal FallbackMultiplierCap = 0m;

    public int EffectiveMaxSessionHours(GuildTrackingSettings? serverDefault = null)
        => MaxSessionHours ?? serverDefault?.MaxSessionHours ?? FallbackMaxSessionHours;

    public int EffectiveActivityRetentionDays(GuildTrackingSettings? serverDefault = null)
        => ActivityRetentionDays ?? serverDefault?.ActivityRetentionDays ?? FallbackActivityRetentionDays;

    public int EffectiveMinTrackedSeconds(GuildTrackingSettings? serverDefault = null)
        => MinTrackedSeconds ?? serverDefault?.MinTrackedSeconds ?? FallbackMinTrackedSeconds;

    public decimal EffectiveMultiplierCap(GuildTrackingSettings? serverDefault = null)
        => MultiplierCap ?? serverDefault?.MultiplierCap ?? FallbackMultiplierCap;
}
