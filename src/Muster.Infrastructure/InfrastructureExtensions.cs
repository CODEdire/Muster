using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Services;

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
        builder.Services.AddScoped<AwardService>();
        builder.Services.AddScoped<EscrowService>();
        builder.Services.AddScoped<MusterService>();
        builder.Services.AddScoped<MissionService>();
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
        builder.Services.AddScoped<CurrencyService>();
        builder.Services.AddScoped<CurrencyAdminService>();
        builder.Services.AddScoped<AuditService>();
        builder.Services.AddScoped<MentionHumanizer>();

        // Platform-independent command services (used by the bot adapters and, later, the web/API).
        builder.Services.AddScoped<AwardCommandService>();
        builder.Services.AddScoped<ScoreCommandService>();
        builder.Services.AddScoped<TrackingCommandService>();
        builder.Services.AddScoped<QuestCommandService>();
        builder.Services.AddScoped<OpCommandService>();
        builder.Services.AddScoped<SeasonCommandService>();
        builder.Services.AddScoped<ConfigCommandService>();
        // Note: MusterCommandService depends on IMusterPublisher (a Discord/bot concern), so it is
        // registered by the bot host alongside its IMusterPublisher implementation — not here.

        return builder;
    }
}
