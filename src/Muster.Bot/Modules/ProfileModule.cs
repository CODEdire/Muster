using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Commands;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>Per-user preferences. The time zone controls how quest start/expiry dates you enter are read.</summary>
public class ProfileModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SlashCommand("timezone", "Set your time zone so quest dates you enter are read correctly (e.g. America/New_York).")]
    public Task<Reply> TimeZoneAsync(
        [SlashCommandParameter(Name = "zone", Description = "IANA time zone id, e.g. America/New_York or Europe/London")] string zone)
        => RunAsync((sp, _) => sp.GetRequiredService<QuestBoardService>().SetTimeZoneAsync(Context.User.Id, zone));
}
