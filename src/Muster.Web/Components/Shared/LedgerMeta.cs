using Muster.Domain.Enums;

namespace Muster.Web.Components.Shared;

/// <summary>UI metadata for ledger sources (label + Material Symbols icon) and the named time-range presets
/// (Today, Yesterday, Last 7 days, …) the wallet/points datagrids use. Keeps the maps in one spot so the
/// guild + personal surfaces stay aligned.</summary>
public static class LedgerMeta
{
    /// <summary>Display label for a ledger source — short, capitalized, friendlier than the raw enum name.</summary>
    public static string SourceLabel(CurrencyLedgerSource source) => source switch
    {
        CurrencyLedgerSource.TrackingSession => "Session",
        CurrencyLedgerSource.Quest => "Quest",
        CurrencyLedgerSource.Muster => "Muster",
        CurrencyLedgerSource.ManualAward => "Award",
        CurrencyLedgerSource.Connector => "Connector",
        CurrencyLedgerSource.Event => "Event",
        CurrencyLedgerSource.Transfer => "Transfer",
        CurrencyLedgerSource.Adjustment => "Adjustment",
        CurrencyLedgerSource.Checkpoint => "Checkpoint",
        CurrencyLedgerSource.Background => "Background",
        CurrencyLedgerSource.Shop => "Shop",
        CurrencyLedgerSource.ShopFee => "Fee",
        _ => source.ToString(),
    };

    /// <summary>Material Symbols icon for a ledger source — picked to read at a glance in a chip.</summary>
    public static string SourceIcon(CurrencyLedgerSource source) => source switch
    {
        CurrencyLedgerSource.TrackingSession => "schedule",
        CurrencyLedgerSource.Quest => "swords",
        CurrencyLedgerSource.Muster => "groups",
        CurrencyLedgerSource.ManualAward => "redeem",
        CurrencyLedgerSource.Connector => "sync_alt",
        CurrencyLedgerSource.Event => "event",
        CurrencyLedgerSource.Transfer => "swap_horiz",
        CurrencyLedgerSource.Adjustment => "tune",
        CurrencyLedgerSource.Checkpoint => "flag",
        CurrencyLedgerSource.Background => "sensors",
        CurrencyLedgerSource.Shop => "shopping_bag",
        CurrencyLedgerSource.ShopFee => "percent",
        _ => "receipt_long",
    };

    /// <summary>Source filter dropdown options in enum order — UI binds this list verbatim.</summary>
    public static readonly IReadOnlyList<CurrencyLedgerSource> AllSources =
        Enum.GetValues<CurrencyLedgerSource>();

    /// <summary>Resolve a named time-range preset into a (from, to) window. <see cref="DateTimeOffset.UtcNow"/> is
    /// the "now" anchor; "today"/"yesterday" use UTC day boundaries (acceptable first cut — refining to the viewer's
    /// zone is a future polish).</summary>
    public static (DateTimeOffset? From, DateTimeOffset? To) ResolveRange(string? preset)
    {
        var now = DateTimeOffset.UtcNow;
        var startToday = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        return preset switch
        {
            "today" => (startToday, null),
            "yesterday" => (startToday.AddDays(-1), startToday),
            "7d" => (now.AddDays(-7), null),
            "30d" => (now.AddDays(-30), null),
            "90d" => (now.AddDays(-90), null),
            _ => (null, null),
        };
    }

    /// <summary>Medal class for a leaderboard rank — gold/silver/bronze for the podium, empty for the rest.</summary>
    public static string RankClass(int rank) => rank switch
    {
        1 => "gold",
        2 => "silver",
        3 => "bronze",
        _ => "",
    };

    /// <summary>Range preset options — dropdown source order.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> RangePresets =
    [
        ("", "All time"),
        ("today", "Today"),
        ("yesterday", "Yesterday"),
        ("7d", "Last 7 days"),
        ("30d", "Last 30 days"),
        ("90d", "Last 90 days"),
    ];
}
