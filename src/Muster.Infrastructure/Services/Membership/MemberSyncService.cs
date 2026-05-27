using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Entities;

namespace Muster.Infrastructure.Services.Membership;

/// <summary>One member's gateway snapshot, used to backfill the roster in bulk.</summary>
public record MemberSnapshot(ulong UserId, string Username, string? GlobalName, string? AvatarHash, string? Nickname, IReadOnlyList<ulong> RoleIds, bool IsBot);

/// <summary>
/// Lazily upserts <see cref="DiscordUser"/> and <see cref="GuildMember"/> records the first time (and
/// each subsequent time) a member is seen acting — a message, reaction, voice state, or command. This
/// avoids needing the privileged GuildMembers intent to bulk-load the roster.
/// </summary>
public class MemberSyncService(MusterDbContext db)
{
    public async Task UpsertAsync(
        ulong guildId, ulong userId, string username, string? globalName, string? avatarHash,
        string? nickname = null, IReadOnlyList<ulong>? roleIds = null, bool isBot = false, CancellationToken ct = default)
    {
        var user = await db.FindUserAsync(userId, ct);
        if (user is null)
        {
            user = new DiscordUser { Id = userId };
            db.Users.Add(user);
        }

        user.Username = username;
        user.GlobalName = globalName;
        user.AvatarHash = avatarHash;
        user.IsBot = isBot;

        var member = await db.FindMemberAsync(guildId, userId, ct);
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

    /// <summary>Upsert just the shared <see cref="DiscordUser"/> profile (no guild membership). Used by the
    /// web login backfill, where we know the signed-in user's identity but not their per-guild roles.</summary>
    public async Task UpsertUserAsync(ulong userId, string username, string? globalName, string? avatarHash, CancellationToken ct = default)
    {
        var user = await db.FindUserAsync(userId, ct);
        if (user is null)
        {
            user = new DiscordUser { Id = userId };
            db.Users.Add(user);
        }

        user.Username = username;
        user.GlobalName = globalName;
        user.AvatarHash = avatarHash;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Ensure a <see cref="DiscordUser"/> row exists with at least a name, from web-login claims.
    /// Never clobbers an avatar or a richer global name already synced from the gateway — only fills gaps,
    /// and saves only when something actually changed. Keeps the signed-in user from showing as a raw id.</summary>
    public async Task EnsureUserAsync(ulong userId, string username, string? globalName = null, CancellationToken ct = default)
    {
        var user = await db.FindUserAsync(userId, ct);
        var changed = false;

        if (user is null)
        {
            user = new DiscordUser { Id = userId };
            db.Users.Add(user);
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(username) && user.Username != username)
        {
            user.Username = username;
            changed = true;
        }

        if (globalName is not null && user.GlobalName != globalName)
        {
            user.GlobalName = globalName;
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Bulk-backfill a guild's roster from gateway snapshots in a single round trip: upserts each
    /// <see cref="DiscordUser"/> profile and <see cref="GuildMember"/> row. Returns the count synced.</summary>
    public async Task<int> SyncGuildAsync(ulong guildId, IReadOnlyCollection<MemberSnapshot> members, CancellationToken ct = default)
    {
        if (members.Count == 0)
        {
            return 0;
        }

        var ids = members.Select(m => m.UserId).ToList();
        var users = await db.UsersByIdAsync(ids, ct);
        var existing = await db.MembersByIdAsync(guildId, ids, ct);

        foreach (var s in members)
        {
            if (!users.TryGetValue(s.UserId, out var user))
            {
                user = new DiscordUser { Id = s.UserId };
                db.Users.Add(user);
                users[s.UserId] = user;
            }

            user.Username = s.Username;
            user.GlobalName = s.GlobalName;
            user.AvatarHash = s.AvatarHash;
            user.IsBot = s.IsBot;

            if (!existing.TryGetValue(s.UserId, out var member))
            {
                member = new GuildMember { GuildId = guildId, UserId = s.UserId, JoinedAt = DateTimeOffset.UtcNow };
                db.GuildMembers.Add(member);
                existing[s.UserId] = member;
            }

            member.Nickname = s.Nickname;
            member.RoleIds = s.RoleIds.ToList();
        }

        await db.SaveChangesAsync(ct);
        return members.Count;
    }

    /// <summary>Remove a member from the guild (they left). The shared DiscordUser and their ledger
    /// history are kept; only the guild membership row is removed.</summary>
    public async Task RemoveMemberAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var member = await db.FindMemberAsync(guildId, userId, ct);
        if (member is not null)
        {
            db.GuildMembers.Remove(member);
            await db.SaveChangesAsync(ct);
        }
    }
}
