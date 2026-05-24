using Microsoft.EntityFrameworkCore;

namespace Muster.Infrastructure.Commands;

public enum RoleKind
{
    Admin,
    Officer,
    Participant,
    QuestManager,
}

/// <summary>
/// Logic for the /config commands that map roles to Discord roles (admin / officer / participant).
/// The guild owner can always run these even before any role is mapped, so the server can be
/// configured without being locked out.
/// </summary>
public class ConfigCommandService(MusterDbContext db)
{
    public Task<CommandResult> ToggleAdminRoleAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
        => ToggleAsync(guildId, roleId, RoleKind.Admin, ct);

    public Task<CommandResult> ToggleOfficerRoleAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
        => ToggleAsync(guildId, roleId, RoleKind.Officer, ct);

    public Task<CommandResult> ToggleParticipantRoleAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
        => ToggleAsync(guildId, roleId, RoleKind.Participant, ct);

    public Task<CommandResult> ToggleQuestManagerRoleAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
        => ToggleAsync(guildId, roleId, RoleKind.QuestManager, ct);

    public async Task<CommandResult> ShowAsync(ulong guildId, CancellationToken ct = default)
    {
        var guild = await db.Guilds.FirstOrDefaultAsync(g => g.Id == guildId, ct);
        if (guild is null)
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        var participants = guild.Settings.ParticipantRoleIds.Count == 0
            ? "_everyone (open)_"
            : Format(guild.Settings.ParticipantRoleIds);

        return CommandResult.Ok(
            $"**Role mapping**\nAdmin roles: {Format(guild.Settings.AdminRoleIds)}\n" +
            $"Officer roles: {Format(guild.Settings.OfficerRoleIds)}\n" +
            $"Participant roles: {participants}\nGuild owner always has admin access.");
    }

    private async Task<CommandResult> ToggleAsync(ulong guildId, ulong roleId, RoleKind kind, CancellationToken ct)
    {
        var guild = await db.Guilds.FirstOrDefaultAsync(g => g.Id == guildId, ct);
        if (guild is null)
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        var current = kind switch
        {
            RoleKind.Admin => guild.Settings.AdminRoleIds,
            RoleKind.Officer => guild.Settings.OfficerRoleIds,
            RoleKind.QuestManager => guild.Settings.QuestManagerRoleIds,
            _ => guild.Settings.ParticipantRoleIds,
        };

        var updated = new List<ulong>(current);
        var added = !updated.Remove(roleId);
        if (added)
        {
            updated.Add(roleId);
        }

        // Reassign to ensure the owned JSON column is detected as changed.
        switch (kind)
        {
            case RoleKind.Admin: guild.Settings.AdminRoleIds = updated; break;
            case RoleKind.Officer: guild.Settings.OfficerRoleIds = updated; break;
            case RoleKind.QuestManager: guild.Settings.QuestManagerRoleIds = updated; break;
            default: guild.Settings.ParticipantRoleIds = updated; break;
        }

        await db.SaveChangesAsync(ct);

        var label = kind.ToString().ToLowerInvariant();
        var note = kind == RoleKind.Participant && updated.Count == 0
            ? " Participation is now open to everyone."
            : string.Empty;

        return CommandResult.Ok((added
            ? $"Added <@&{roleId}> as a {label} role."
            : $"Removed <@&{roleId}> from {label} roles.") + note);
    }

    private static string Format(List<ulong> roleIds)
        => roleIds.Count == 0 ? "_none_" : string.Join(", ", roleIds.Select(r => $"<@&{r}>"));
}
