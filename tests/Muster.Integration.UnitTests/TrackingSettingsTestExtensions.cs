using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities.Guilds;
using Muster.Persistence;

namespace Muster.IntegrationTests;

/// <summary>Test helper: seed/mutate a guild's <see cref="GuildTrackingSettings"/> row (tracking config moved out of
/// the GuildSettings JSON blob into its own table).</summary>
internal static class TrackingSettingsTestExtensions
{
    public static async Task SeedTrackingAsync(this MusterDbContext db, ulong guildId, Action<GuildTrackingSettings> mutate)
    {
        var row = await db.GuildTrackingSettings.FirstOrDefaultAsync(t => t.GuildId == guildId);
        if (row is null)
        {
            row = new GuildTrackingSettings { GuildId = guildId };
            db.GuildTrackingSettings.Add(row);
        }

        mutate(row);
        await db.SaveChangesAsync();
    }
}
