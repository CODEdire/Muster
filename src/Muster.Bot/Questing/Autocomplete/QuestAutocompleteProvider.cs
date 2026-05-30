using Muster.Bot.Platform.Telemetry;
using Muster.Contracts;
using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Muster.Infrastructure.Services.Membership;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Questing.Autocomplete;

/// <summary>
/// Suggests active quests (any origin) by name for quest id parameters, so users never type a GUID.
/// Filters by viewer access: quest managers see all active states (incl. mod-only Disputed / PendingApproval /
/// PendingFinal); everyone else only sees publicly-visible quests (Open / Scheduled) plus quests they own or
/// participate in. Without this filter, the dropdown leaks names of in-progress moderator work to non-managers.
/// </summary>
public class QuestAutocompleteProvider(IServiceScopeFactory scopeFactory)
    : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        using var _ = BotTelemetry.MeasureAutocomplete(nameof(QuestAutocompleteProvider));

        if (context.Interaction.GuildId is not { } guildId)
        {
            return [];
        }

        var input = option.Value ?? string.Empty;
        var viewerId = context.Interaction.User.Id;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();
        var isManager = await scope.ServiceProvider.GetRequiredService<GuildAuthorizationService>()
            .IsQuestManagerAsync(guildId, viewerId);

        // Base: every active-ish state (Open, Scheduled, Disputed, PendingApproval, PendingFinal).
        var query = db.Quests
            .Where(m => m.GuildId == guildId
                && (m.Status == QuestStatus.Open || m.Status == QuestStatus.Scheduled || m.Status == QuestStatus.Disputed
                    || m.Status == QuestStatus.PendingApproval || m.Status == QuestStatus.PendingFinal)
                && m.Name.Contains(input));

        // Non-managers don't see mod-only states unless they're personally involved — owner or any-participant.
        // (Open and Scheduled are public board states; the rest are mod workflow.)
        if (!isManager)
        {
            query = query.Where(m =>
                m.Status == QuestStatus.Open
                || m.Status == QuestStatus.Scheduled
                || m.OwnerId == viewerId
                || m.Participants.Any(p => p.UserId == viewerId));
        }

        var quests = await query
            .OrderByDescending(m => m.CreatedAt)
            .Take(25)
            .Select(m => new { m.Id, m.Name, m.Origin })
            .ToListAsync();

        return quests.Select(q => new ApplicationCommandOptionChoiceProperties(
            $"{q.Name} [{(q.Origin == QuestOrigin.Guild ? "Guild" : "Personal")}]", q.Id.ToString()));
    }
}
