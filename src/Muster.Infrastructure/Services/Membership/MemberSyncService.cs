using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Muster.Domain.Entities;

namespace Muster.Infrastructure.Services.Membership;

/// <summary>
/// Lazily upserts <see cref="DiscordUser"/> and <see cref="GuildMember"/> records the first time (and
/// each subsequent time) a member is seen acting — a message, reaction, voice state, or command. This
/// avoids needing the privileged GuildMembers intent to bulk-load the roster.
/// </summary>
public class MemberSyncService(MusterDbContext db)
{
    public async Task UpsertAsync(
        ulong guildId, ulong userId, string username, string? globalName, string? avatarHash,
        string? nickname = null, IReadOnlyList<ulong>? roleIds = null, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            user = new DiscordUser { Id = userId };
            db.Users.Add(user);
        }

        user.Username = username;
        user.GlobalName = globalName;
        user.AvatarHash = avatarHash;

        var member = await db.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId, ct);
        if (member is null)
        {
            member = new GuildMember { GuildId = guildId, UserId = userId, JoinedAt = DateTimeOffset.UtcNow };
            db.GuildMembers.Add(member);
        }

        if (nickname is not null)
        {
            member.Nickname = nickname;
        }

        if (roleIds is not null)
        {
            member.RoleIds = roleIds.ToList();
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Remove a member from the guild (they left). The shared DiscordUser and their ledger
    /// history are kept; only the guild membership row is removed.</summary>
    public async Task RemoveMemberAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var member = await db.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId, ct);
        if (member is not null)
        {
            db.GuildMembers.Remove(member);
            await db.SaveChangesAsync(ct);
        }
    }
}
