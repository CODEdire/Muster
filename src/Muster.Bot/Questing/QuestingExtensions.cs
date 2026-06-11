using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Muster.Infrastructure.Messaging;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Hosting.Services.ComponentInteractions;
using NetCord.Services.ComponentInteractions;

namespace Muster.Bot.Questing;

/// <summary>
/// Per-feature composition for the Questing slice. <see cref="AddQuestingFeature"/> registers
/// schedulers + autocompletes; <see cref="UseQuestingModule"/> registers the slash command module
/// plus the three component-interaction modules that handle button/menu/modal interactions on quest
/// cards.
/// </summary>
public static class QuestingExtensions
{
    public static IHostApplicationBuilder AddQuestingFeature(this IHostApplicationBuilder builder)
    {
        // Publishes the periodic quest reconciliation tick (activate scheduled / expire past-deadline).
        // The tick is handled on the cluster leader only (see WolverineExtensions), so this is safe to run
        // here even if the bot scales out. Owned by Infrastructure but registered on the bot host because
        // the downstream handler needs the gateway/REST client.
        builder.Services.AddHostedService<QuestSweepScheduler>();

        // Prunes completed quest cards from the channel board after the guild's retention window
        // (bot-only — needs Discord REST).
        builder.Services.AddHostedService<QuestBoardCleanupScheduler>();

        // DMs "closes soon" deadline nudges to active workers / a taker-less bounty owner
        // (bot-only — needs DM REST).
        builder.Services.AddHostedService<QuestReminderScheduler>();

        // Autocomplete providers for quest-related slash command parameters.
        builder.Services.AddTransient<QuestAutocompleteProvider>();
        builder.Services.AddTransient<QuestCurrencyAutocompleteProvider>();
        builder.Services.AddTransient<QuestTypeAutocompleteProvider>();

        return builder;
    }

    public static IHost UseQuestingModule(this IHost host)
    {
        // Slash command surface for posting/managing quests.
        host.AddApplicationCommandModule<QuestModule>();

        // Component interaction surfaces for the quest card: claim/submit buttons, taker select menu,
        // dispute/intake/feedback modals. Each kind has its own context type per NetCord's component
        // routing.
        host.AddComponentInteractionModule<ButtonInteractionContext, QuestInteractionModule>();
        host.AddComponentInteractionModule<StringMenuInteractionContext, QuestMenuInteractionModule>();
        host.AddComponentInteractionModule<ModalInteractionContext, QuestModalInteractionModule>();

        return host;
    }
}
