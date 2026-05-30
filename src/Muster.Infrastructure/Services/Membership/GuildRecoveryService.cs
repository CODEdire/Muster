using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Muster.Domain.Entities.Guilds;
using Muster.Domain.Entities.Members;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Services.Membership;

/// <summary>
/// Lazy self-heal for guilds the bot's GuildCreate handler missed (bot was offline, crashed mid-sync,
/// or wasn't deployed yet when a Discord admin first hit the web). When the web detects "no Guild row
/// for the guild the user is asking about", this calls Discord REST with the bot token to materialise
/// just enough state — the Guild + roles + the visiting user's membership — so the existing
/// IsAdminAsync / GuildPageComponentBase access checks can proceed normally.
///
/// <para>Scoped narrowly: we provision <i>just the visiting user's</i> membership, not the whole guild
/// member list — full sync stays the bot's job (via gateway events + the SyncGuildMembers Wolverine
/// flow). This is enough to let the admin in and configure the guild; the bot will fill in the rest
/// once it's caught up.</para>
///
/// <para>Returns false (without writing anything) when the bot isn't in the guild OR the visiting user
/// isn't actually a Discord admin of it — never grants access beyond what Discord already permits.</para>
/// </summary>
public class GuildRecoveryService(
    MusterDbContext db,
    IHttpClientFactory httpFactory,
    IConfiguration config,
    GuildProvisioningService provisioning,
    ILogger<GuildRecoveryService> logger)
{
    private const string DiscordApiBase = "https://discord.com/api/v10";

    // Discord permission bits we care about for admin recovery. Admin (0x8) and Manage Guild (0x20)
    // are the same bits used by the rest of the auth pipeline (HasAdminPermissionAsync).
    private const ulong AdministratorPermission = 0x00000008UL;
    private const ulong ManageGuildPermission   = 0x00000020UL;
    private const ulong AdminBypassMask = AdministratorPermission | ManageGuildPermission;

    /// <summary>
    /// Ensures the guild + the visiting user's membership exist in our DB, fetching from Discord with
    /// the bot token if not already present. Returns true when the user is a real Discord admin of the
    /// guild (= safe to grant the Muster admin tier); false otherwise. Callers should run the normal
    /// auth check after this — this method writes the minimum data the normal check needs to succeed.
    /// </summary>
    public async Task<bool> TryRecoverAdminAccessAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var token = config["Discord:Token"];
        if (string.IsNullOrWhiteSpace(token))
        {
            // Without a bot token we can't call Discord REST — bail rather than throw mid-render.
            logger.LogWarning("GuildRecoveryService skipped (no Discord:Token configured).");
            return false;
        }

        using var http = httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token);
        http.BaseAddress = new Uri(DiscordApiBase);

        // 1. Confirm the bot can see the guild. If GET /guilds/{id} 404s the bot isn't in it →
        //    we can't recover (and shouldn't pretend to).
        var guildResp = await http.GetAsync($"/guilds/{guildId}", ct);
        if (!guildResp.IsSuccessStatusCode)
        {
            logger.LogInformation("Recovery skipped for guild {Guild}: Discord returned {Status}.", guildId, guildResp.StatusCode);
            return false;
        }

        var guildJson = await guildResp.Content.ReadFromJsonAsync<DiscordGuild>(cancellationToken: ct);
        if (guildJson is null || guildJson.Id != guildId.ToString())
        {
            return false;
        }

        // 2. Fetch the visiting user's membership — gives us their roles + nickname. 404 = user isn't
        //    in the guild (or bot lacks the Members intent, but that surfaces as 403 instead).
        var memberResp = await http.GetAsync($"/guilds/{guildId}/members/{userId}", ct);
        if (!memberResp.IsSuccessStatusCode)
        {
            logger.LogInformation("Recovery skipped for guild {Guild} user {User}: member fetch returned {Status}.",
                guildId, userId, memberResp.StatusCode);
            return false;
        }

        var memberJson = await memberResp.Content.ReadFromJsonAsync<DiscordMember>(cancellationToken: ct);
        if (memberJson is null)
        {
            return false;
        }

        // 3. Compute the user's effective Discord permission bitfield = union of every role they hold.
        //    @everyone (the role whose id equals the guild id) is implicit on every member; include its
        //    permission bits too. We only need this snapshot to decide whether to admit them — the rest
        //    of the data layer doesn't compute perms from this method.
        var ownerId = ulong.TryParse(guildJson.OwnerId, out var oid) ? oid : 0UL;
        var memberRoleIds = (memberJson.Roles ?? [])
            .Select(r => ulong.TryParse(r, out var rid) ? rid : 0UL)
            .Where(r => r != 0)
            .ToList();

        ulong effectivePerms = 0UL;
        foreach (var role in guildJson.Roles ?? [])
        {
            if (!ulong.TryParse(role.Id, out var rid)) continue;
            if (!ulong.TryParse(role.Permissions, out var perms)) continue;
            // @everyone always applies; other roles only if the member holds them.
            if (rid == guildId || memberRoleIds.Contains(rid))
            {
                effectivePerms |= perms;
            }
        }

        var isOwner = ownerId != 0 && ownerId == userId;
        var hasAdminBits = (effectivePerms & AdminBypassMask) != 0;
        if (!isOwner && !hasAdminBits)
        {
            // User is in the guild but isn't an admin → don't provision on their behalf (they couldn't
            // do anything with the access anyway, and provisioning here would let any member trigger a
            // DB write just by hitting the URL).
            logger.LogInformation("Recovery denied for guild {Guild} user {User}: not a Discord admin.", guildId, userId);
            return false;
        }

        // 4. Provision the guild (idempotent — uses the existing service so currency + season seeding
        //    matches the GuildCreate handler path) + upsert the relevant role snapshots + the visiting
        //    user's GuildMember row. Only the user's own membership; bot's full member sync will fill
        //    the rest later.
        await provisioning.EnsureGuildAsync(guildId, guildJson.Name ?? string.Empty, guildJson.Icon, ownerId, ct);

        await UpsertRolesAsync(guildId, guildJson.Roles ?? [], ct);
        await UpsertMemberAsync(guildId, userId, memberJson, ct);

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Recovered admin access for guild {Guild} user {User} (owner={Owner}, adminBits={Bits:X}).",
            guildId, userId, isOwner, effectivePerms & AdminBypassMask);
        return true;
    }

    private async Task UpsertRolesAsync(ulong guildId, IReadOnlyList<DiscordRole> roles, CancellationToken ct)
    {
        // Pull all existing role rows for this guild in one shot, then patch/insert.
        var existing = await db.GuildRoles.Where(r => r.GuildId == guildId).ToListAsync(ct);
        var byId = existing.ToDictionary(r => r.RoleId);

        foreach (var role in roles)
        {
            if (!ulong.TryParse(role.Id, out var roleId)) continue;
            ulong.TryParse(role.Permissions, out var perms);

            if (byId.TryGetValue(roleId, out var row))
            {
                row.Name = role.Name ?? string.Empty;
                row.Permissions = perms;
            }
            else
            {
                db.GuildRoles.Add(new GuildRole
                {
                    GuildId = guildId,
                    RoleId = roleId,
                    Name = role.Name ?? string.Empty,
                    Permissions = perms,
                });
            }
        }
    }

    private async Task UpsertMemberAsync(ulong guildId, ulong userId, DiscordMember member, CancellationToken ct)
    {
        var existing = await db.FindMemberAsync(guildId, userId, ct);
        var roleIds = (member.Roles ?? [])
            .Select(r => ulong.TryParse(r, out var rid) ? rid : 0UL)
            .Where(r => r != 0)
            .ToList();

        if (existing is null)
        {
            db.GuildMembers.Add(new GuildMember
            {
                GuildId = guildId,
                UserId = userId,
                Nickname = member.Nick,
                RoleIds = roleIds,
                JoinedAt = DateTimeOffset.TryParse(member.JoinedAt, out var ja) ? ja : DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.Nickname = member.Nick;
            existing.RoleIds = roleIds;
        }
    }

    // Minimal Discord REST DTOs — only the fields we read here. Extending them is fine; keep names
    // matching Discord's JSON casing (snake_case via JsonPropertyName attributes on the converter).
    private sealed record DiscordGuild(
        [property: System.Text.Json.Serialization.JsonPropertyName("id")] string? Id,
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("icon")] string? Icon,
        [property: System.Text.Json.Serialization.JsonPropertyName("owner_id")] string? OwnerId,
        [property: System.Text.Json.Serialization.JsonPropertyName("roles")] List<DiscordRole>? Roles);

    private sealed record DiscordRole(
        [property: System.Text.Json.Serialization.JsonPropertyName("id")] string? Id,
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("permissions")] string? Permissions);

    private sealed record DiscordMember(
        [property: System.Text.Json.Serialization.JsonPropertyName("nick")] string? Nick,
        [property: System.Text.Json.Serialization.JsonPropertyName("roles")] List<string>? Roles,
        [property: System.Text.Json.Serialization.JsonPropertyName("joined_at")] string? JoinedAt);
}
