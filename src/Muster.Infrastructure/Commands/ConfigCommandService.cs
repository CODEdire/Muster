using Microsoft.EntityFrameworkCore;

namespace Muster.Infrastructure.Commands;

/// <summary>
/// Logic for the /config commands that map app permissions to Discord roles
/// (GuildSettings.AdminRoleIds / OfficerRoleIds). The guild owner can always run these even before any
/// role is mapped, so the server can be configured without being locked out.
/// </summary>
public class ConfigCommandService(MusterDbContext db)
{
    public Task<CommandResult> ToggleAdminRoleAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
        => ToggleAsync(guildId, roleId, admin: true, ct);

    public Task<CommandResult> ToggleOfficerRoleAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
        => ToggleAsync(guildId, roleId, admin: false, ct);

    public async Task<CommandResult> ShowAsync(ulong guildId, CancellationToken ct = default)
    {
        var guild = await db.Guilds.FirstOrDefaultAsync(g => g.Id == guildId, ct);
        if (guild is null)
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        var admins = Format(guild.Settings.AdminRoleIds);
        var officers = Format(guild.Settings.OfficerRoleIds);
        return CommandResult.Ok(
            $"**Role mapping**\nAdmin roles: {admins}\nOfficer roles: {officers}\nGuild owner always has admin access.");
    }

    private async Task<CommandResult> ToggleAsync(ulong guildId, ulong roleId, bool admin, CancellationToken ct)
    {
        var guild = await db.Guilds.FirstOrDefaultAsync(g => g.Id == guildId, ct);
        if (guild is null)
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        var current = admin ? guild.Settings.AdminRoleIds : guild.Settings.OfficerRoleIds;
        var updated = new List<ulong>(current);
        var added = !updated.Remove(roleId);
        if (added)
        {
            updated.Add(roleId);
        }

        // Reassign to ensure the owned JSON column is detected as changed.
        if (admin)
        {
            guild.Settings.AdminRoleIds = updated;
        }
        else
        {
            guild.Settings.OfficerRoleIds = updated;
        }

        await db.SaveChangesAsync(ct);

        var label = admin ? "admin" : "officer";
        return CommandResult.Ok(added
            ? $"Added <@&{roleId}> as an {label} role."
            : $"Removed <@&{roleId}> from {label} roles.");
    }

    private static string Format(List<ulong> roleIds)
        => roleIds.Count == 0 ? "_none_" : string.Join(", ", roleIds.Select(r => $"<@&{r}>"));
}
