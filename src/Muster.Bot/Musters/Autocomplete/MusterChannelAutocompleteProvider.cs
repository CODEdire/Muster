using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Platform.Telemetry;
using Muster.Domain.Enums;
using Muster.Persistence;
using Muster.Persistence.Queries;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Musters.Autocomplete;

/// <summary>
/// Suggests channels a muster may be posted to: the guild's allow-list when set, otherwise every chat-capable
/// channel (text + voice text). Keeps the <c>/muster post</c> channel option in sync with the allow-list (the
/// CreateMuster funnel also rejects a disallowed channel as a backstop).
/// </summary>
public class MusterChannelAutocompleteProvider(IServiceScopeFactory scopeFactory)
    : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        using var _ = BotTelemetry.MeasureAutocomplete(nameof(MusterChannelAutocompleteProvider));

        if (context.Interaction.GuildId is not { } guildId)
        {
            return [];
        }

        var input = option.Value ?? string.Empty;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();

        var allowed = (await db.GetSettingsAsync(guildId)).Musters.AllowedChannelIds;

        var query = db.GuildChannels
            .Where(c => c.GuildId == guildId && c.DeletedAt == null
                && (c.Kind == GuildChannelKind.Text || c.Kind == GuildChannelKind.Voice)
                && c.Name.Contains(input));

        if (allowed.Count > 0)
        {
            query = query.Where(c => allowed.Contains(c.ChannelId));
        }

        var channels = await query
            .OrderBy(c => c.Name)
            .Take(25)
            .Select(c => new { c.ChannelId, c.Name })
            .ToListAsync();

        return channels.Select(c => new ApplicationCommandOptionChoiceProperties($"#{c.Name}", c.ChannelId.ToString()));
    }
}
