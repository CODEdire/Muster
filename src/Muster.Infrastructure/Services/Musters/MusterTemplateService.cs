using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities.Musters;
using Muster.Persistence;

namespace Muster.Infrastructure.Services.Musters;

/// <summary>CRUD for a guild's muster templates (admin config). Mutations are guild-scoped; the web admin calls this
/// directly, like the other config services.</summary>
public class MusterTemplateService(MusterDbContext db)
{
    public Task<List<MusterTemplate>> ListAsync(ulong guildId, bool includeDisabled = true, CancellationToken ct = default)
        => db.MusterTemplates
            .Where(t => t.GuildId == guildId && (includeDisabled || t.Enabled))
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

    public Task<MusterTemplate?> GetAsync(ulong guildId, Guid id, CancellationToken ct = default)
        => db.MusterTemplates.FirstOrDefaultAsync(t => t.Id == id && t.GuildId == guildId, ct);

    /// <summary>Create (Id empty) or update an existing template. Returns the saved row, or null if an update targeted
    /// a template that doesn't belong to the guild.</summary>
    public async Task<MusterTemplate?> SaveAsync(MusterTemplate input, CancellationToken ct = default)
    {
        if (input.Id == Guid.Empty)
        {
            input.Id = Guid.NewGuid();
            db.MusterTemplates.Add(input);
            await db.SaveChangesAsync(ct);
            return input;
        }

        var row = await db.MusterTemplates.FirstOrDefaultAsync(t => t.Id == input.Id && t.GuildId == input.GuildId, ct);
        if (row is null)
        {
            return null;
        }

        row.Name = input.Name;
        row.Description = input.Description;
        row.Title = input.Title;
        row.Prompt = input.Prompt;
        row.Points = input.Points;
        row.Coins = input.Coins;
        row.CoinCurrencyId = input.CoinCurrencyId;
        row.RetentionHours = input.RetentionHours;
        row.Capacity = input.Capacity;
        row.ExpiryHours = input.ExpiryHours;
        row.Enabled = input.Enabled;
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<bool> DeleteAsync(ulong guildId, Guid id, CancellationToken ct = default)
        => await db.MusterTemplates.Where(t => t.Id == id && t.GuildId == guildId).ExecuteDeleteAsync(ct) > 0;
}
