namespace Muster.Infrastructure.Services.Currencies;

/// <summary>Platform-wide ledger-retention policy, bound from configuration (section <c>Currency</c>).</summary>
public sealed class CurrencyRetentionOptions
{
    /// <summary>Platform ceiling + default for ledger history retention, in days. 0 = no platform limit (guilds
    /// keep history forever unless they set their own retention). When &gt; 0 it both caps every guild's value and
    /// is the default for guilds that haven't set one.</summary>
    public int MaxLedgerRetentionDays { get; set; }
}

/// <summary>Pure rules combining a guild's <c>LedgerRetentionDays</c> with the platform cap. Reused by the prune
/// sweep (effective window) and by config surfaces (input validation), so they can't drift.</summary>
public static class LedgerRetention
{
    /// <summary>The retention window actually applied to a guild: the platform global is both a cap and a default.
    /// 0 (from either) means "no limit" for that side; the result is 0 only when neither imposes a limit.</summary>
    public static int Effective(int guildDays, int globalMaxDays)
    {
        var g = Math.Max(0, guildDays);
        var max = Math.Max(0, globalMaxDays);
        if (max == 0)
        {
            return g;          // no platform limit → the guild's own value (0 = forever)
        }

        return g == 0 ? max : Math.Min(g, max); // guild unset → inherit; else clamp to the cap
    }

    /// <summary>Whether a requested guild value exceeds the platform cap (used to reject config input).</summary>
    public static bool ExceedsCap(int guildDays, int globalMaxDays) => globalMaxDays > 0 && guildDays > globalMaxDays;
}
