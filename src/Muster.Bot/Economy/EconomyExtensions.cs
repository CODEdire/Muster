using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetCord.Hosting.Services.ApplicationCommands;

namespace Muster.Bot.Economy;

/// <summary>
/// Per-feature composition for the Economy slice (currency balances, ledger retention, autocomplete).
/// <see cref="UseEconomyModule"/> registers the two slash command modules — Currency (mint/spend/transfer)
/// and Score (read-only leaderboards).
/// </summary>
public static class EconomyExtensions
{
    public static IHostApplicationBuilder AddEconomyFeature(this IHostApplicationBuilder builder)
    {
        // Periodically reconciles External/Hybrid balances from their connectors (per-currency sync interval).
        builder.Services.AddHostedService<CurrencyBalanceSyncScheduler>();

        // Daily: compacts ledger history beyond each guild's LedgerRetentionDays into carry-forward checkpoints.
        builder.Services.AddHostedService<LedgerPruneScheduler>();

        // Autocomplete provider for currency codes (used by config, scoring, and other features).
        builder.Services.AddTransient<CurrencyAutocompleteProvider>();

        return builder;
    }

    public static IHost UseEconomyModule(this IHost host)
    {
        host.AddApplicationCommandModule<CurrencyModule>();
        host.AddApplicationCommandModule<ScoreModule>();
        return host;
    }
}
