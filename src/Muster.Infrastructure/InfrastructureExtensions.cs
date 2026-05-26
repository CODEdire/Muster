using Microsoft.Extensions.DependencyInjection;
using Muster.Persistence;
using Microsoft.Extensions.Hosting;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Services.Ledger;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Musters;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Quests;
using Muster.Infrastructure.Services.Events;
using Muster.Infrastructure.Services.Seasons;
using Muster.Infrastructure.Services.Tracking;
using Muster.Infrastructure.Services.Web;
using Muster.Infrastructure.Commands.Ledger;
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
        builder.Services.AddScoped<ScoreQueryService>();
        builder.Services.AddScoped<IQuestService, QuestService>();
        builder.Services.AddScoped<IQuestAuthorizer, QuestAuthorizer>();
        builder.Services.AddScoped<IQuestReadService, QuestReadService>();
        builder.Services.AddScoped<MusterService>();
        builder.Services.AddScoped<GuildEventService>();
        builder.Services.AddScoped<TrackingSessionService>();
        builder.Services.AddScoped<ManualAwardService>();
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
        builder.Services.AddScoped<CurrencyAdminService>();
        builder.Services.AddScoped<AuditService>();
        builder.Services.AddScoped<MentionHumanizer>();
        builder.Services.AddScoped<TimeZoneService>();

        // Platform-independent command services (used by the bot adapters and, later, the web/API).
        builder.Services.AddScoped<AwardCommandService>();
        builder.Services.AddScoped<ScoreCommandService>();
        builder.Services.AddScoped<TrackingCommandService>();
        builder.Services.AddScoped<OpCommandService>();
        builder.Services.AddScoped<SeasonCommandService>();
        builder.Services.AddScoped<ConfigCommandService>();
        builder.Services.AddScoped<QuestMaintenanceService>();
        // Quest lifecycle moments publish as Wolverine messages (QuestLifecycleNotified); a Discord/connector
        // consumer subscribes later. The currency-event sink default just logs until the outbox connector lands.
        builder.Services.AddScoped<ICurrencyEventSink, LoggingCurrencyEventSink>();
        // Note: MusterCommandService depends on IMusterPublisher (a Discord/bot concern), so it is
        // registered by the bot host alongside its IMusterPublisher implementation — not here.

        return builder;
    }
}
