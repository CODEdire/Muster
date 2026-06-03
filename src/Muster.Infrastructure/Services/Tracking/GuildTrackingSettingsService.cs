using Microsoft.Extensions.Options;
using Muster.Domain.Entities.Guilds;
using Muster.Persistence;

namespace Muster.Infrastructure.Services.Tracking;

/// <summary>
/// Reads and writes a guild's <see cref="GuildTrackingSettings"/> row. When a guild has no row yet, reads return a
/// defaults instance built from <c>IOptions&lt;GuildTrackingSettings&gt;</c> (bound from AppConfig / appsettings under
/// <c>GuildDefaults:Tracking</c>) — so callers always get a usable settings object, and the same defaults seed the row
/// the first time it's written. Mirrors <c>GuildMusterSettingsService</c> (the table-per-feature + configured-bootstrap
/// pattern).
/// </summary>
public class GuildTrackingSettingsService(MusterDbContext db, IOptions<GuildTrackingSettings> defaults)
{
    /// <summary>The guild's row, or a (non-persisted) defaults instance when none exists.</summary>
    public async Task<GuildTrackingSettings> GetAsync(ulong guildId, CancellationToken ct = default)
        => await db.GuildTrackingSettings.FindAsync([guildId], ct) ?? Defaults(guildId);

    /// <summary>Load the guild's row (seeding it from defaults if missing), apply <paramref name="mutate"/>, save.</summary>
    public async Task<GuildTrackingSettings> UpsertAsync(ulong guildId, Action<GuildTrackingSettings> mutate, CancellationToken ct = default)
    {
        var row = await db.GuildTrackingSettings.FindAsync([guildId], ct);
        if (row is null)
        {
            row = Defaults(guildId);
            db.GuildTrackingSettings.Add(row);
        }

        mutate(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>A new settings instance seeded from the configured platform defaults (GuildId stamped on).</summary>
    private GuildTrackingSettings Defaults(ulong guildId)
    {
        var d = defaults.Value;
        return new GuildTrackingSettings
        {
            GuildId = guildId,
            BackgroundTrackingOptIn = d.BackgroundTrackingOptIn,
            SessionCoinCurrencyCode = d.SessionCoinCurrencyCode,
            MinutesPerCoin = d.MinutesPerCoin,
            PointsPerVoiceMinute = d.PointsPerVoiceMinute,
            DefaultBackgroundGuards = d.DefaultBackgroundGuards,
            DefaultSessionGuards = d.DefaultSessionGuards,
            DefaultEventGuards = d.DefaultEventGuards,
            MaxSessionHours = d.MaxSessionHours,
            ActivityRetentionDays = d.ActivityRetentionDays,
            MinTrackedSeconds = d.MinTrackedSeconds,
            MultiplierStacking = d.MultiplierStacking,
            MultiplierCap = d.MultiplierCap,
            SessionStartBonus = d.SessionStartBonus,
            SessionEndBonus = d.SessionEndBonus,
            StartBonusWindowMinutes = d.StartBonusWindowMinutes,
            EndBonusWindowMinutes = d.EndBonusWindowMinutes,
            MultiplyPresenceBonuses = d.MultiplyPresenceBonuses,
        };
    }
}
