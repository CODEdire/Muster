using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Services.Shops;

/// <summary>Applies the versioned guild seed (<see cref="GuildSeed"/>) to a guild on demand — the manual counterpart
/// to the auto-seed in <c>GuildProvisioningService</c>. <c>force</c> restores every missing default (intentional
/// re-add of deleted ones); otherwise only newer-than-recorded entries are added.</summary>
public class GuildSeedService(MusterDbContext db)
{
    /// <summary>Returns how many default rows were added.</summary>
    public async Task<int> ApplyAsync(ulong guildId, bool force, CancellationToken ct = default)
    {
        var guild = await db.FindGuildAsync(guildId, ct);
        if (guild is null)
        {
            return 0;
        }

        var added = await GuildSeed.ApplyAsync(db, guild, force, ct);
        await db.SaveChangesAsync(ct); // SeedVersion may have moved even when nothing new was added
        return added;
    }
}
