using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities.Quests;
using Muster.Persistence;

namespace Muster.Infrastructure.Services.Quests;

/// <summary>
/// Admin CRUD for a guild's quest-type vocabulary (mirrors the shop's store-type management on <c>ShopService</c>).
/// Quest types are a small, admin-curated controlled vocabulary; the type's Material icon is the quest card's visual.
/// </summary>
public class QuestTypeService(MusterDbContext db)
{
    /// <summary>A guild's quest types, ordered for pickers (Sort then Name). Untracked.</summary>
    public Task<List<QuestType>> ListAsync(ulong guildId, CancellationToken ct = default)
        => db.QuestTypes.AsNoTracking().Where(t => t.GuildId == guildId)
            .OrderBy(t => t.Sort).ThenBy(t => t.Name).ToListAsync(ct);

    /// <summary>A tracked quest type by id within the guild (for edit/delete), or null.</summary>
    public Task<QuestType?> FindAsync(ulong guildId, Guid id, CancellationToken ct = default)
        => db.QuestTypes.FirstOrDefaultAsync(t => t.GuildId == guildId && t.Id == id, ct);

    /// <summary>Create a quest type. Returns null on a blank/duplicate name (unique per guild).</summary>
    public async Task<QuestType?> CreateAsync(ulong guildId, string name, int sort, string? icon, CancellationToken ct = default)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0 || await db.QuestTypes.AnyAsync(t => t.GuildId == guildId && t.Name == name, ct))
        {
            return null;
        }

        var type = QuestType.Create(guildId, name, sort, NullIfBlank(icon));
        db.QuestTypes.Add(type);
        await db.SaveChangesAsync(ct);
        return type;
    }

    /// <summary>Rename / re-sort / re-icon a quest type. Returns false on a blank or duplicate name.</summary>
    public async Task<bool> EditAsync(QuestType type, string name, int sort, string? icon, CancellationToken ct = default)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0
            || await db.QuestTypes.AnyAsync(t => t.GuildId == type.GuildId && t.Name == name && t.Id != type.Id, ct))
        {
            return false;
        }

        type.Name = name;
        type.Sort = sort;
        type.Icon = NullIfBlank(icon);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Delete a quest type, detaching any quests using it (no FK cascade — null it so none dangle).</summary>
    public async Task DeleteAsync(QuestType type, CancellationToken ct = default)
    {
        var quests = await db.Quests.Where(q => q.QuestTypeId == type.Id).ToListAsync(ct);
        foreach (var q in quests)
        {
            q.QuestTypeId = null;
        }

        db.QuestTypes.Remove(type);
        await db.SaveChangesAsync(ct);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
