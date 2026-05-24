using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Commands;
using NetCord;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>
/// Discord adapter for configuring the role mapping (admin-only). The guild owner always passes the
/// admin gate, so a server can be configured from scratch without being locked out.
/// </summary>
public class ConfigModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SlashCommand("config-admin-role", "Toggle whether a Discord role grants bot admin.")]
    public Task<string> AdminRoleAsync(
        [SlashCommandParameter(Name = "role", Description = "Role to toggle")] Role role)
        => RunAsync((sp, guildId) => sp.GetRequiredService<ConfigCommandService>().ToggleAdminRoleAsync(guildId, role.Id), RequiredRole.Admin, "config.adminRole");

    [SlashCommand("config-officer-role", "Toggle whether a Discord role grants officer permissions.")]
    public Task<string> OfficerRoleAsync(
        [SlashCommandParameter(Name = "role", Description = "Role to toggle")] Role role)
        => RunAsync((sp, guildId) => sp.GetRequiredService<ConfigCommandService>().ToggleOfficerRoleAsync(guildId, role.Id), RequiredRole.Admin, "config.officerRole");

    [SlashCommand("config-participant-role", "Toggle a role allowed to participate. No roles set = everyone participates.")]
    public Task<string> ParticipantRoleAsync(
        [SlashCommandParameter(Name = "role", Description = "Role to toggle")] Role role)
        => RunAsync((sp, guildId) => sp.GetRequiredService<ConfigCommandService>().ToggleParticipantRoleAsync(guildId, role.Id), RequiredRole.Admin, "config.participantRole");

    [SlashCommand("config-show", "Show the current role mapping.")]
    public Task<string> ShowAsync()
        => RunAsync((sp, guildId) => sp.GetRequiredService<ConfigCommandService>().ShowAsync(guildId), RequiredRole.Admin);
}
