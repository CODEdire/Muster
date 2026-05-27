using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Autocomplete;
using Muster.Infrastructure.Commands;
using NetCord;
using NetCord.Services.ApplicationCommands;
using Muster.Infrastructure.Commands.Membership;

namespace Muster.Bot.Modules;

/// <summary>
/// Discord adapter for configuring the role mapping (admin-only). The guild owner always passes the
/// admin gate, so a server can be configured from scratch without being locked out.
/// </summary>
public class ConfigModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SlashCommand("config-admin-role", "Toggle whether a Discord role grants bot admin.")]
    public Task AdminRoleAsync(
        [SlashCommandParameter(Name = "role", Description = "Role to toggle")] Role role)
        => RunAsync((sp, guildId) => sp.GetRequiredService<ConfigCommandService>().ToggleAdminRoleAsync(guildId, role.Id), RequiredRole.Admin, "config.adminRole");

    [SlashCommand("config-officer-role", "Toggle whether a Discord role grants officer permissions.")]
    public Task OfficerRoleAsync(
        [SlashCommandParameter(Name = "role", Description = "Role to toggle")] Role role)
        => RunAsync((sp, guildId) => sp.GetRequiredService<ConfigCommandService>().ToggleOfficerRoleAsync(guildId, role.Id), RequiredRole.Admin, "config.officerRole");

    [SlashCommand("config-participant-role", "Toggle a role allowed to participate. No roles set = everyone participates.")]
    public Task ParticipantRoleAsync(
        [SlashCommandParameter(Name = "role", Description = "Role to toggle")] Role role)
        => RunAsync((sp, guildId) => sp.GetRequiredService<ConfigCommandService>().ToggleParticipantRoleAsync(guildId, role.Id), RequiredRole.Admin, "config.participantRole");

    [SlashCommand("config-questmanager-role", "Toggle a role that can create guild quests and arbitrate personal quests.")]
    public Task QuestManagerRoleAsync(
        [SlashCommandParameter(Name = "role", Description = "Role to toggle")] Role role)
        => RunAsync((sp, guildId) => sp.GetRequiredService<ConfigCommandService>().ToggleQuestManagerRoleAsync(guildId, role.Id), RequiredRole.Admin, "config.questManagerRole");

    [SlashCommand("config-ledger-retention", "Set how many days of detailed ledger history to keep (0 = platform default).")]
    public Task LedgerRetentionAsync(
        [SlashCommandParameter(Name = "days", Description = "Days of detail to keep before compacting to checkpoints (0 = platform default)")] int days)
        => RunAsync((sp, guildId) => sp.GetRequiredService<ConfigCommandService>().SetLedgerRetentionAsync(guildId, days), RequiredRole.Admin, "config.ledgerRetention");

    [SlashCommand("config-background-tracking", "Set background tracking to opt-in (members must opt in) or opt-out (on by default).")]
    public Task BackgroundTrackingAsync(
        [SlashCommandParameter(Name = "opt-in", Description = "true = members must opt in; false = on by default (members may opt out)")] bool optIn)
        => RunAsync((sp, guildId) => sp.GetRequiredService<ConfigCommandService>().SetBackgroundOptInAsync(guildId, optIn), RequiredRole.Admin, "config.backgroundTracking");

    [SlashCommand("config-session-coin", "Set which spendable currency sessions mint on close, and the minutes-per-coin rate.")]
    public Task SessionCoinAsync(
        [SlashCommandParameter(Name = "currency", Description = "Spendable currency code to mint (leave blank to disable)",
            AutocompleteProviderType = typeof(CurrencyAutocompleteProvider))] string? currency = null,
        [SlashCommandParameter(Name = "minutes-per-coin", Description = "Eligible minutes per 1 coin (0 = disable)")] int minutesPerCoin = 0)
        => RunAsync((sp, guildId) => sp.GetRequiredService<ConfigCommandService>().SetSessionCoinAsync(guildId, currency, minutesPerCoin), RequiredRole.Admin, "config.sessionCoin");

    [SlashCommand("config-show", "Show the current role mapping.")]
    public Task ShowAsync()
        => RunAsync((sp, guildId) => sp.GetRequiredService<ConfigCommandService>().ShowAsync(guildId), RequiredRole.Admin);
}
