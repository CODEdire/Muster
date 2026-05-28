using Muster.Domain.Entities.Tracking;
using Muster.Domain.Enums;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Commands.Tracking;

/// <summary>Platform-independent CRUD for a guild's reward multipliers (one-off windows, recurring schedules,
/// per-role boosts). Backs the web Multipliers page.</summary>
public class RewardMultiplierCommandService(MusterDbContext db)
{
    public Task<List<RewardMultiplier>> ListAsync(ulong guildId, CancellationToken ct = default)
        => db.ListMultipliersAsync(guildId, ct);

    public async Task<CommandResult> AddOneOffAsync(
        ulong guildId, string name, decimal factor, MultiplierScope scope,
        DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken ct = default,
        int minPeople = 0, int minMinutes = 0)
    {
        if (endsAt <= startsAt)
        {
            return CommandResult.Error("The end time must be after the start time.");
        }

        return await AddAsync(new RewardMultiplier
        {
            GuildId = guildId, Kind = MultiplierKind.OneOff,
            StartsAt = startsAt, EndsAt = endsAt,
        }, name, factor, scope, ct, minPeople, minMinutes);
    }

    public async Task<CommandResult> AddRecurringAsync(
        ulong guildId, string name, decimal factor, MultiplierScope scope,
        WeekDays days, TimeOnly startTime, TimeOnly endTime, CancellationToken ct = default,
        int minPeople = 0, int minMinutes = 0)
    {
        if (days == WeekDays.None)
        {
            return CommandResult.Error("Pick at least one day for a recurring multiplier.");
        }

        return await AddAsync(new RewardMultiplier
        {
            GuildId = guildId, Kind = MultiplierKind.Recurring,
            Days = days, StartTime = startTime, EndTime = endTime,
        }, name, factor, scope, ct, minPeople, minMinutes);
    }

    public async Task<CommandResult> AddRoleAsync(
        ulong guildId, string name, decimal factor, MultiplierScope scope, ulong roleId, CancellationToken ct = default)
    {
        if (roleId == 0)
        {
            return CommandResult.Error("Pick a role for a role multiplier.");
        }

        return await AddAsync(new RewardMultiplier
        {
            GuildId = guildId, Kind = MultiplierKind.Role, RoleId = roleId,
        }, name, factor, scope, ct);
    }

    public async Task<CommandResult> SetEnabledAsync(ulong guildId, Guid id, bool enabled, CancellationToken ct = default)
    {
        var m = await db.FindMultiplierAsync(guildId, id, ct);
        if (m is null)
        {
            return CommandResult.Error("That multiplier doesn't exist.");
        }

        m.Enabled = enabled;
        await db.SaveChangesAsync(ct);
        return CommandResult.Ok(enabled ? $"Enabled **{m.Name}**." : $"Disabled **{m.Name}**.");
    }

    public async Task<CommandResult> RemoveAsync(ulong guildId, Guid id, CancellationToken ct = default)
    {
        var m = await db.FindMultiplierAsync(guildId, id, ct);
        if (m is null)
        {
            return CommandResult.Error("That multiplier doesn't exist.");
        }

        db.RewardMultipliers.Remove(m);
        await db.SaveChangesAsync(ct);
        return CommandResult.Ok($"Removed **{m.Name}**.");
    }

    private async Task<CommandResult> AddAsync(
        RewardMultiplier m, string name, decimal factor, MultiplierScope scope, CancellationToken ct,
        int minPeople = 0, int minMinutes = 0)
    {
        if (factor <= 0m)
        {
            return CommandResult.Error("The factor must be greater than 0 (use values below 1 to de-emphasise, above 1 to boost).");
        }

        if (scope == MultiplierScope.None)
        {
            return CommandResult.Error("Pick at least one reward plane for the multiplier to apply to.");
        }

        m.Id = Guid.NewGuid();
        m.Name = string.IsNullOrWhiteSpace(name) ? $"{m.Kind} ×{factor}" : name.Trim();
        m.Factor = factor;
        m.Scope = scope;
        m.MinPeopleInChannel = Math.Max(0, minPeople);
        m.MinMinutes = Math.Max(0, minMinutes);
        m.Enabled = true;

        db.RewardMultipliers.Add(m);
        await db.SaveChangesAsync(ct);
        return CommandResult.Ok($"Added **{m.Name}** (×{factor}).");
    }
}
