using Muster.Domain.Entities.Guilds;
using Muster.Persistence;

namespace Muster.Infrastructure.Services.Shops;

/// <summary>
/// The versioned catalog of default data a guild ships with — shop categories + store types, the spendable COIN
/// currency, and starter muster templates. Coordinates the per-area stagers and owns the shared
/// <see cref="Guild.SeedVersion"/> bump so a single int tracks everything a guild has been seeded with.
///
/// <para>Each stager is name/code-guarded and version-gated: on a re-run only entries whose <c>IntroducedIn</c> is
/// newer than the guild's recorded version are added (so new defaults reach existing guilds without resurrecting
/// ones an admin deleted). Pass <c>force: true</c> to re-add every missing default regardless of version (the
/// "restore defaults" path). The caller saves.</para>
/// </summary>
public static class GuildSeed
{
    /// <summary>Bump when a new default is introduced; set its stager entry's <c>IntroducedIn</c> to this value so
    /// existing guilds pick it up on their next seed pass.</summary>
    public const int CurrentVersion = 3;

    /// <summary>Stage every area's missing defaults onto the context and advance the guild's seed version. Returns the
    /// total rows added across all areas.</summary>
    public static async Task<int> ApplyAsync(MusterDbContext db, Guild guild, bool force, CancellationToken ct = default)
    {
        var from = guild.SeedVersion;
        var added = 0;

        added += await ShopSeed.StageAsync(db, guild.Id, from, force, ct);
        added += await Quests.QuestTypeSeed.StageAsync(db, guild.Id, from, force, ct);
        added += await CurrencySeed.StageAsync(db, guild.Id, from, force, ct);
        added += await MusterTemplateSeed.StageAsync(db, guild.Id, from, force, ct);

        guild.SeedVersion = Math.Max(guild.SeedVersion, CurrentVersion);
        return added;
    }
}
