using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities.Musters;
using Muster.Persistence;

namespace Muster.Infrastructure.Services.Shops;

/// <summary>
/// Default muster templates a guild starts with so a Muster Creator can post something useful on day one
/// (self-service posting is template-locked, so an empty template list means nothing to post). Idempotent:
/// skips by name and (unless <c>force</c>) only adds entries newer than the guild's recorded seed version.
/// Part of the <see cref="GuildSeed"/> catalog.
/// </summary>
public static class MusterTemplateSeed
{
    private record SeedTemplate(string Name, string? Title, string? Prompt, long Points, int RetentionHours, int IntroducedIn);

    private static readonly SeedTemplate[] Templates =
    [
        new("Raid", "Raid forming up", "React to join the raid.", 100, 48, 2),
        new("Event", "Event starting", "React if you're attending.", 50, 48, 2),
        new("Patrol", "Patrol underway", "React to check in for patrol.", 25, 24, 2),
    ];

    /// <summary>Stage the guild's missing default muster templates. Returns rows added.</summary>
    public static async Task<int> StageAsync(MusterDbContext db, ulong guildId, int seedFrom, bool force, CancellationToken ct = default)
    {
        var added = 0;

        var existing = (await db.MusterTemplates.Where(t => t.GuildId == guildId).Select(t => t.Name).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var s in Templates)
        {
            if ((force || s.IntroducedIn > seedFrom) && existing.Add(s.Name))
            {
                db.MusterTemplates.Add(new MusterTemplate
                {
                    Id = Guid.NewGuid(),
                    GuildId = guildId,
                    Name = s.Name,
                    Title = s.Title,
                    Prompt = s.Prompt,
                    Points = s.Points,
                    Coins = 0,
                    CoinCurrencyId = null,
                    RetentionHours = s.RetentionHours,
                    Enabled = true,
                });
                added++;
            }
        }

        return added;
    }
}
