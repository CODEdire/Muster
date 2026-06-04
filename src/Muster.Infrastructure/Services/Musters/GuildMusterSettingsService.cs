using Microsoft.Extensions.Options;
using Muster.Domain.Entities.Guilds;
using Muster.Persistence;

namespace Muster.Infrastructure.Services.Musters;

/// <summary>
/// Reads and writes a guild's <see cref="GuildMusterSettings"/> row. When a guild has no row yet, reads return a
/// defaults instance built from <c>IOptions&lt;GuildMusterSettings&gt;</c> (bound from AppConfig / appsettings under
/// <c>GuildDefaults:Musters</c>) — so callers always get a usable settings object, and the same defaults seed the row
/// the first time it's written. This is the table-per-feature + configured-bootstrap pattern.
/// </summary>
public class GuildMusterSettingsService(MusterDbContext db, IOptions<GuildMusterSettings> defaults)
{
    /// <summary>The guild's row, or a (non-persisted) defaults instance when none exists.</summary>
    public async Task<GuildMusterSettings> GetAsync(ulong guildId, CancellationToken ct = default)
        => await db.GuildMusterSettings.FindAsync([guildId], ct) ?? Defaults(guildId);

    /// <summary>Load the guild's row (seeding it from defaults if missing), apply <paramref name="mutate"/>, save.</summary>
    public async Task<GuildMusterSettings> UpsertAsync(ulong guildId, Action<GuildMusterSettings> mutate, CancellationToken ct = default)
    {
        var row = await db.GuildMusterSettings.FindAsync([guildId], ct);
        if (row is null)
        {
            row = Defaults(guildId);
            db.GuildMusterSettings.Add(row);
        }

        mutate(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>A new settings instance seeded from the configured platform defaults (GuildId stamped on).</summary>
    private GuildMusterSettings Defaults(ulong guildId)
    {
        var d = defaults.Value;
        return new GuildMusterSettings
        {
            GuildId = guildId,
            MusterChannelId = d.MusterChannelId,
            BoardRetentionHours = d.BoardRetentionHours,
            AutoCreateOnSession = d.AutoCreateOnSession,
            AllowedChannelIds = [.. d.AllowedChannelIds],
        };
    }
}
