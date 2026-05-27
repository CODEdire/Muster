using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Autocomplete;
using Muster.Domain.Enums;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Commands.Tracking;
using Muster.Infrastructure.Services.Platform;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>Per-user preferences. The time zone controls how quest start/expiry dates you enter are read.</summary>
public class ProfileModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SlashCommand("track-privacy", "Choose how this server tracks your participation.")]
    public Task PrivacyAsync(
        [SlashCommandParameter(Name = "choice", Description = "Default = follow server; In = opt in; BackgroundOut = no passive tracking; AllOut = none")]
        TrackingChoice choice)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<TrackingPreferenceCommandService>().SetAsync(guildId, Context.User.Id, choice),
            RequiredRole.None);

    [SlashCommand("timezone", "Show your time zone — or set it by picking one (controls how quest dates you enter are read).")]
    public Task TimeZoneAsync(
        [SlashCommandParameter(Name = "zone", Description = "Pick a zone to set it — leave empty to see your current one",
            AutocompleteProviderType = typeof(TimezoneAutocompleteProvider))] string? zone = null)
        => RunAsync(async (sp, guildId) =>
        {
            var tz = sp.GetRequiredService<TimeZoneService>();

            // No argument → show the current setting.
            if (string.IsNullOrWhiteSpace(zone))
            {
                var mine = await tz.GetUserZoneAsync(Context.User.Id);
                if (TimeZoneService.IsValidZone(mine))
                {
                    return CommandResult.Ok($"🕒 Your time zone is **{mine}**. Change it by running `/timezone` and picking another.");
                }

                var effective = await tz.ResolveZoneIdAsync(guildId, Context.User.Id);
                return CommandResult.Ok(
                    $"You haven't set a time zone — dates default to the server's (**{effective}**). " +
                    "Run `/timezone` and pick yours from the list.");
            }

            // Argument → set it.
            var (ok, error) = await tz.SetUserZoneAsync(Context.User.Id, zone);
            return ok
                ? CommandResult.Ok($"Your time zone is now **{zone.Trim()}** — quest dates you enter will be read in that zone.")
                : CommandResult.Error(error!);
        });
}
