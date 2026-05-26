using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;

namespace Muster.Persistence.Queries;

/// <summary>Queries over guilds, members, roles, and users.</summary>
public static class MembershipQueries
{
    /// <summary>Find a guild (tracked) by id.</summary>
    public static Task<Guild?> FindGuildAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.Guilds.FirstOrDefaultAsync(g => g.Id == guildId, ct);

    /// <summary>A user's display name (global name, else username), or null if unknown — for connector enrichment.</summary>
    public static Task<string?> FindDisplayNameAsync(this MusterDbContext db, ulong userId, CancellationToken ct = default)
        => db.Users.Where(u => u.Id == userId).Select(u => u.GlobalName ?? u.Username).FirstOrDefaultAsync(ct);

    /// <summary>A guild's settings (read-only, untracked); defaults when the guild is unknown.</summary>
    public static async Task<GuildSettings> GetSettingsAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => (await db.Guilds.AsNoTracking().FirstOrDefaultAsync(g => g.Id == guildId, ct))?.Settings ?? new();

    /// <summary>All active guilds (for cross-guild sweeps).</summary>
    public static Task<List<Guild>> ListActiveGuildsAsync(this MusterDbContext db, CancellationToken ct = default)
        => db.Guilds.Where(g => g.IsActive).ToListAsync(ct);

    /// <summary>A guild's configured IANA time zone id (scalar), or null.</summary>
    public static Task<string?> GuildTimeZoneIdAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.Guilds.Where(g => g.Id == guildId).Select(g => g.TimeZoneId).FirstOrDefaultAsync(ct);

    // --- Members / roles / users ---

    /// <summary>Find a guild member (tracked).</summary>
    public static Task<GuildMember?> FindMemberAsync(this MusterDbContext db, ulong guildId, ulong userId, CancellationToken ct = default)
        => db.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId, ct);

    /// <summary>Permission bitmasks for a set of the guild's roles (client-side bit tests follow).</summary>
    public static Task<List<ulong>> RolePermissionsAsync(this MusterDbContext db, ulong guildId, List<ulong> roleIds, CancellationToken ct = default)
        => db.GuildRoles.Where(r => r.GuildId == guildId && roleIds.Contains(r.RoleId)).Select(r => r.Permissions).ToListAsync(ct);

    /// <summary>Find a guild role (tracked).</summary>
    public static Task<GuildRole?> FindRoleAsync(this MusterDbContext db, ulong guildId, ulong roleId, CancellationToken ct = default)
        => db.GuildRoles.FirstOrDefaultAsync(r => r.GuildId == guildId && r.RoleId == roleId, ct);

    /// <summary>All of a guild's roles (tracked).</summary>
    public static Task<List<GuildRole>> ListRolesAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.GuildRoles.Where(r => r.GuildId == guildId).ToListAsync(ct);

    /// <summary>Map of role id → name for the given roles in a guild.</summary>
    public static Task<Dictionary<ulong, string>> RoleNameMapAsync(this MusterDbContext db, ulong guildId, List<ulong> roleIds, CancellationToken ct = default)
        => db.GuildRoles.Where(r => r.GuildId == guildId && roleIds.Contains(r.RoleId)).ToDictionaryAsync(r => r.RoleId, r => r.Name, ct);

    /// <summary>Find a user (tracked).</summary>
    public static Task<DiscordUser?> FindUserAsync(this MusterDbContext db, ulong userId, CancellationToken ct = default)
        => db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

    /// <summary>A user's stored IANA time zone id (scalar), or null.</summary>
    public static Task<string?> UserTimeZoneIdAsync(this MusterDbContext db, ulong userId, CancellationToken ct = default)
        => db.Users.Where(u => u.Id == userId).Select(u => u.TimeZoneId).FirstOrDefaultAsync(ct);

    /// <summary>Whether the user has opted out of currency-receipt DMs (false = receipts on; default when unknown).</summary>
    public static Task<bool> CurrencyDmOptOutAsync(this MusterDbContext db, ulong userId, CancellationToken ct = default)
        => db.Users.Where(u => u.Id == userId).Select(u => u.CurrencyDmOptOut).FirstOrDefaultAsync(ct);

    /// <summary>Set the user's currency-receipt DM opt-out. No-op if the user is unknown. Returns the saved value.</summary>
    public static async Task<bool> SetCurrencyDmOptOutAsync(this MusterDbContext db, ulong userId, bool optOut, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return false;
        }

        user.CurrencyDmOptOut = optOut;
        await db.SaveChangesAsync(ct);
        return optOut;
    }

    /// <summary>Map of user id → display name (global name, falling back to username) for the given users.</summary>
    public static Task<Dictionary<ulong, string>> UserDisplayNameMapAsync(this MusterDbContext db, List<ulong> userIds, CancellationToken ct = default)
        => db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.GlobalName ?? u.Username, ct);

    /// <summary>The subset of the given user ids that are bot/app accounts (for filtering human-facing lists).</summary>
    public static async Task<HashSet<ulong>> BotUserIdsAsync(this MusterDbContext db, List<ulong> userIds, CancellationToken ct = default)
        => (await db.Users.Where(u => userIds.Contains(u.Id) && u.IsBot).Select(u => u.Id).ToListAsync(ct)).ToHashSet();

    /// <summary>Whether the user is a bot/app account that's a member of the guild (a valid API service actor).</summary>
    public static Task<bool> IsGuildBotAsync(this MusterDbContext db, ulong guildId, ulong userId, CancellationToken ct = default)
        => (from u in db.Users
            where u.Id == userId && u.IsBot
            join m in db.GuildMembers on u.Id equals m.UserId
            where m.GuildId == guildId
            select u.Id).AnyAsync(ct);

    /// <summary>All of a guild's members (tracked-free read).</summary>
    public static Task<List<GuildMember>> ListMembersAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.GuildMembers.Where(m => m.GuildId == guildId).ToListAsync(ct);

    /// <summary>The guild ids a user is a member of.</summary>
    public static Task<List<ulong>> ListMemberGuildIdsAsync(this MusterDbContext db, ulong userId, CancellationToken ct = default)
        => db.GuildMembers.Where(m => m.UserId == userId).Select(m => m.GuildId).ToListAsync(ct);

    /// <summary>Active guilds a user owns or belongs to, by name.</summary>
    public static Task<List<Guild>> ListActiveGuildsForUserAsync(this MusterDbContext db, ulong userId, List<ulong> memberGuildIds, CancellationToken ct = default)
        => db.Guilds.Where(g => g.IsActive && (g.OwnerId == userId || memberGuildIds.Contains(g.Id)))
            .OrderBy(g => g.Name).ToListAsync(ct);

    /// <summary>Whether the user owns the guild.</summary>
    public static Task<bool> IsGuildOwnerAsync(this MusterDbContext db, ulong guildId, ulong userId, CancellationToken ct = default)
        => db.Guilds.AnyAsync(g => g.Id == guildId && g.OwnerId == userId, ct);

    /// <summary>The guild owner's user id (0 if the guild is missing).</summary>
    public static Task<ulong> GuildOwnerIdAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.Guilds.Where(g => g.Id == guildId).Select(g => g.OwnerId).FirstOrDefaultAsync(ct);

    /// <summary>Whether the user is a member of the guild.</summary>
    public static Task<bool> IsMemberAsync(this MusterDbContext db, ulong guildId, ulong userId, CancellationToken ct = default)
        => db.GuildMembers.AnyAsync(m => m.GuildId == guildId && m.UserId == userId, ct);
}
