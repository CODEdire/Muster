using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services.ApplicationCommands;

namespace Muster.Bot.Tracking;

/// <summary>
/// Per-feature composition for the Tracking slice. <see cref="AddTrackingFeature"/> registers
/// schedulers, the per-guild reconcile coordinator, autocomplete providers, and the gateway handlers
/// that drive voice/message/channel/event tracking; <see cref="UseTrackingModule"/> registers the
/// slash command module.
/// </summary>
public static class TrackingExtensions
{
    public static IHostApplicationBuilder AddTrackingFeature(this IHostApplicationBuilder builder)
    {
        // Periodically flushes always-on background voice accrual for still-present members
        // (idempotent, gateway-cache-driven).
        builder.Services.AddHostedService<BackgroundFlushScheduler>();

        // Reconciles voice accrual at reward-multiplier window edges so each segment is credited at
        // one exact regime.
        builder.Services.AddHostedService<MultiplierBoundaryScheduler>();

        // Daily: prunes raw activity records beyond each guild's retention window (rollups kept).
        builder.Services.AddHostedService<ActivityPruneScheduler>();

        // Per-guild voice reconcile coordinator: debounces event bursts + serializes reconciles per guild.
        builder.Services.AddSingleton<GuildReconcileCoordinator>();

        // Autocomplete providers for active sessions + reward multipliers.
        builder.Services.AddTransient<ActiveSessionAutocompleteProvider>();

        // Discord gateway handlers driving the various tracking signals.
        builder.Services.AddGatewayHandler<ChannelLifecycleHandler>();
        builder.Services.AddGatewayHandler<MessageActivityHandler>();
        builder.Services.AddGatewayHandler<ScheduledEventHandler>();
        builder.Services.AddGatewayHandler<VoiceAttendanceHandler>();

        return builder;
    }

    public static IHost UseTrackingModule(this IHost host)
    {
        host.AddApplicationCommandModule<TrackModule>();
        return host;
    }
}
