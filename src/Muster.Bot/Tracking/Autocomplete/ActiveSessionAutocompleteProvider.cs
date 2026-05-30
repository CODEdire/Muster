using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Platform.Telemetry;
using Muster.Persistence;
using Muster.Domain.Enums;
using NetCord;
using NetCord.Services.ApplicationCommands;
using NetCord.Rest;

namespace Muster.Bot.Tracking.Autocomplete;

/// <summary>Suggests the guild's currently-active tracking sessions by name, so /track-stop never needs a GUID
/// and only ever offers sessions that can actually be stopped.</summary>
public class ActiveSessionAutocompleteProvider(IServiceScopeFactory scopeFactory)
    : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        using var _ = BotTelemetry.MeasureAutocomplete(nameof(ActiveSessionAutocompleteProvider));

        if (context.Interaction.GuildId is not { } guildId)
        {
            return [];
        }

        var input = option.Value ?? string.Empty;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();

        var sessions = await db.TrackingSessions
            .Where(s => s.GuildId == guildId && s.Status == TrackingSessionStatus.Active && s.Name.Contains(input))
            .OrderBy(s => s.StartedAt)
            .Take(25)
            .Select(s => new { s.Id, s.Name, s.VoiceChannelId })
            .ToListAsync();

        return sessions.Select(s => new ApplicationCommandOptionChoiceProperties(
            $"{s.Name} (#{s.VoiceChannelId})", s.Id.ToString()));
    }
}
