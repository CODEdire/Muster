using Microsoft.Extensions.Hosting;
using Muster.Bot.Musters.Modules;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Hosting.Services.ComponentInteractions;
using NetCord.Services.ComponentInteractions;

namespace Muster.Bot.Musters;

/// <summary>
/// Per-feature composition for the muster (reaction check-in) slice. The backend (entity, CQRS funnel, service) lives
/// in Muster.Infrastructure; the live card is rendered by <c>MusterBoardNotificationHandler</c> (a Wolverine handler,
/// discovered automatically). <see cref="UseMustersModule"/> registers the slash command + the Check-In button module.
/// </summary>
public static class MustersExtensions
{
    public static IHostApplicationBuilder AddMustersFeature(this IHostApplicationBuilder builder)
    {
        // Autocomplete for the muster id parameters (so staff pick a muster, never type a GUID).
        builder.Services.AddTransient<MusterAutocompleteProvider>();
        builder.Services.AddTransient<RecentMusterAutocompleteProvider>();
        builder.Services.AddTransient<MusterChannelAutocompleteProvider>();

        // Auto-expires open, non-linked musters past their window (pays out + flips the card terminal).
        builder.Services.AddHostedService<MusterExpirySweepScheduler>();

        // Prunes terminal muster cards from the channel after the guild's retention window (bot-only — needs REST).
        builder.Services.AddHostedService<MusterBoardCleanupScheduler>();

        return builder;
    }

    public static IHost UseMustersModule(this IHost host)
    {
        host.AddApplicationCommandModule<MusterModule>();
        host.AddComponentInteractionModule<ButtonInteractionContext, MusterInteractionModule>();
        return host;
    }
}
