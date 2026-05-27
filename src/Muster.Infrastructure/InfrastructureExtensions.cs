using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Muster.Persistence;
using Microsoft.Extensions.Hosting;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Musters;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Quests;
using Muster.Infrastructure.Services.Events;
using Muster.Infrastructure.Services.Seasons;
using Muster.Infrastructure.Services.Tracking;
using Muster.Infrastructure.Services.Web;
using Muster.Infrastructure.Commands.Membership;
using Muster.Infrastructure.Commands.Musters;
using Muster.Infrastructure.Commands.Quests;
using Muster.Infrastructure.Commands.Events;
using Muster.Infrastructure.Commands.Seasons;
using Muster.Infrastructure.Commands.Tracking;

namespace Muster.Infrastructure;

public static class InfrastructureExtensions
{
    /// <summary>
    /// Registers <see cref="MusterDbContext"/> against the Aspire-provided connection plus the
    /// domain services. In Azure the connection string carries no password (Entra managed identity /
    /// Active Directory Default).
    /// </summary>
    public static TBuilder AddMusterInfrastructure<TBuilder>(this TBuilder builder, string connectionName = "musterdb")
        where TBuilder : IHostApplicationBuilder
    {
        builder.AddSqlServerDbContext<MusterDbContext>(connectionName);

        builder.Services.AddScoped<GuildProvisioningService>();
        builder.Services.AddScoped<ICurrencyReadService, CurrencyReadService>();
        builder.Services.AddScoped<IQuestService, QuestService>();
        builder.Services.AddScoped<IQuestAuthorizer, QuestAuthorizer>();
        builder.Services.AddScoped<IQuestReadService, QuestReadService>();
        builder.Services.AddScoped<MusterService>();
        builder.Services.AddScoped<GuildEventService>();
        builder.Services.AddScoped<TrackingSessionService>();
        builder.Services.AddScoped<BackgroundTrackingService>();
        builder.Services.AddScoped<ActivityService>();
        builder.Services.AddScoped<SeasonService>();
        builder.Services.AddScoped<MemberSyncService>();
        builder.Services.AddScoped<RoleSyncService>();
        builder.Services.AddScoped<GuildAuthorizationService>();
        builder.Services.AddScoped<WebGuildService>();
        builder.Services.AddScoped<WebAdminService>();
        builder.Services.AddScoped<WebMemberService>();
        builder.Services.AddScoped<ApiClientService>();
        builder.Services.AddScoped<ICurrencyService, CurrencyService>();
        builder.Services.AddScoped<ICurrencyAuthorizer, Services.Currencies.CurrencyAuthorizer>();
        builder.Services.AddScoped<Services.Currencies.ICurrencyBulkService, Services.Currencies.CurrencyBulkService>();
        // Platform-wide ledger retention cap/default (appsettings "Currency:MaxLedgerRetentionDays"; 0 = unlimited).
        builder.Services.Configure<Services.Currencies.CurrencyRetentionOptions>(builder.Configuration.GetSection("Currency"));
        builder.Services.AddScoped<Services.Currencies.ILedgerPruneService, LedgerPruneService>();
        builder.Services.AddScoped<Services.Currencies.ICurrencyAdminService, CurrencyAdminService>();
        builder.Services.AddScoped<AuditService>();
        builder.Services.AddScoped<MentionHumanizer>();
        builder.Services.AddScoped<TimeZoneService>();

        // Platform-independent command services (used by the bot adapters and, later, the web/API).
        builder.Services.AddScoped<TrackingCommandService>();
        builder.Services.AddScoped<TrackedChannelCommandService>();
        builder.Services.AddScoped<TrackingPreferenceCommandService>();
        builder.Services.AddScoped<OpCommandService>();
        builder.Services.AddScoped<SeasonCommandService>();
        builder.Services.AddScoped<ConfigCommandService>();
        builder.Services.AddScoped<Services.Quests.IQuestMaintenanceService, QuestMaintenanceService>();
        // Quest + currency events publish as Wolverine messages (QuestLifecycleNotified, CurrencyMovementRecorded);
        // a Discord/connector consumer subscribes later. A logging handler is the default seam (CurrencyService
        // publishes through IMessageBus, registered by Wolverine in the bot/web hosts).

        // Outbound currency connectors: one configurable HTTP client per currency (auth + signing + Credit/Debit/
        // GetBalance actions). Secrets are encrypted via Data Protection — registered separately by the hosts that
        // need it (AddMusterConnectorProtection), NOT here: the migration host also calls this method, and Data
        // Protection's startup hosted service would query the keys table before migrations create it.
        // Resilience for connector calls (retries + circuit breaker + concurrency limiter). HttpClient.Timeout is
        // disabled so the per-connector timeout (a linked CTS in the client) governs the overall budget.
        builder.Services.AddHttpClient(Connectors.CurrencyConnectorClient.ClientName)
            .ConfigureHttpClient(c => c.Timeout = System.Threading.Timeout.InfiniteTimeSpan)
            .AddStandardResilienceHandler();
        builder.Services.AddScoped<Connectors.ICurrencyConnectorClient, Connectors.CurrencyConnectorClient>();
        builder.Services.AddScoped<Connectors.CurrencyConnectorSyncService>();

        // Outbound currency webhooks: per-guild signed POST of every movement (CurrencyMovementRecorded fan-out).
        // Typed HttpClient + resilience; the dispatcher signs/sends, the service does admin CRUD. Like connectors,
        // the secret protector comes from AddMusterConnectorProtection (web + bot only).
        builder.Services.AddHttpClient<Services.Currencies.CurrencyWebhookDispatcher>()
            .AddStandardResilienceHandler();
        builder.Services.AddScoped<Services.Currencies.ICurrencyWebhookService, Services.Currencies.CurrencyWebhookService>();
        // Note: MusterCommandService depends on IMusterPublisher (a Discord/bot concern), so it is
        // registered by the bot host alongside its IMusterPublisher implementation — not here.

        return builder;
    }

    /// <summary>
    /// Registers Data Protection (key ring persisted to the DB, shared via a fixed application name) + the connector
    /// secret protector. Call only from hosts that read/write connector secrets — <b>web and bot</b> — which start
    /// after migrations (the key ring is read at startup, so the <c>DataProtectionKeys</c> table must already exist).
    /// The migration host deliberately omits it.
    /// </summary>
    public static TBuilder AddMusterConnectorProtection<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddSingleton<Connectors.IConnectorSecretProtector, Connectors.ConnectorSecretProtector>();
        builder.Services.AddDataProtection()
            .PersistKeysToDbContext<MusterDbContext>()
            .SetApplicationName("Muster");

        return builder;
    }
}
