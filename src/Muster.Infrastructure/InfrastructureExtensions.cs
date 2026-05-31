using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Muster.Infrastructure.Messaging;
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
        builder.Services.AddHttpClient(); // GuildRecoveryService uses IHttpClientFactory for Discord REST
        builder.Services.AddScoped<GuildRecoveryService>();
        builder.Services.AddScoped<ICurrencyReadService, CurrencyReadService>();
        builder.Services.AddScoped<IQuestService, QuestService>();
        builder.Services.AddScoped<IQuestAuthorizer, QuestAuthorizer>();
        builder.Services.AddScoped<IQuestReadService, QuestReadService>();
        builder.Services.AddScoped<MusterService>();
        builder.Services.AddScoped<Services.Musters.IMusterReadService, Services.Musters.MusterReadService>();
        builder.Services.AddScoped<Services.Musters.GuildMusterSettingsService>();
        builder.Services.AddScoped<Services.Musters.MusterTemplateService>();
        // Platform defaults for a guild's muster settings (AppConfig / appsettings) — seed new rows + fill read-misses.
        builder.Services.Configure<Domain.Entities.Guilds.GuildMusterSettings>(
            builder.Configuration.GetSection("GuildDefaults:Musters"));
        builder.Services.AddScoped<GuildEventService>();
        builder.Services.AddScoped<TrackingSessionService>();
        builder.Services.AddScoped<BackgroundTrackingService>();
        builder.Services.AddScoped<RewardMultiplierService>();
        builder.Services.AddScoped<ParticipationReadService>();
        builder.Services.AddScoped<ActivityService>();
        builder.Services.AddScoped<SeasonService>();
        builder.Services.AddScoped<MemberSyncService>();
        builder.Services.AddScoped<RoleSyncService>();
        builder.Services.AddScoped<ChannelSyncService>();
        builder.Services.AddSingleton<Platform.WebLinkBuilder>();
        builder.Services.AddScoped<GuildAuthorizationService>();
        builder.Services.AddScoped<WebGuildService>();
        builder.Services.AddScoped<WebAdminService>();
        builder.Services.AddScoped<WebMemberService>();
        // Wallet vs Points read surfaces — same storage, distinct callers. WalletReadService never returns POINTS;
        // PointsReadService only ever returns POINTS. Pages/Discord/API never share a "currency reader" any more.
        builder.Services.AddScoped<WalletReadService>();
        builder.Services.AddScoped<PointsReadService>();
        builder.Services.AddScoped<ApiClientService>();
        builder.Services.AddScoped<ICurrencyService, CurrencyService>();
        builder.Services.AddScoped<ICurrencyAuthorizer, Services.Currencies.CurrencyAuthorizer>();
        builder.Services.AddScoped<Services.Currencies.ICurrencyBulkService, Services.Currencies.CurrencyBulkService>();
        // Platform-wide ledger retention cap/default (appsettings "Currency:MaxLedgerRetentionDays"; 0 = unlimited).
        builder.Services.Configure<Services.Currencies.CurrencyRetentionOptions>(builder.Configuration.GetSection("Currency"));
        builder.Services.AddScoped<Services.Currencies.ILedgerPruneService, LedgerPruneService>();
        builder.Services.AddScoped<Services.Currencies.ICurrencyAdminService, CurrencyAdminService>();
        builder.Services.AddScoped<AuditService>();
        builder.Services.AddScoped<IAuditPruneService, AuditPruneService>();
        // Audit retention window from "Audit:RetentionDays" (default 90; 0 = unlimited).
        builder.Services.Configure<AuditRetentionOptions>(builder.Configuration.GetSection("Audit"));
        // Fallback audit origin so AuditService never fails to resolve in a host that forgot to register one.
        // Hosts (Web, Bot) replace this with their real origin via Services.AddSingleton<IAuditOriginProvider>.
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<IAuditOriginProvider>(
            builder.Services, _ => new AuditOriginProvider(Muster.Domain.Enums.AuditOrigin.System));
        builder.Services.AddScoped<MentionHumanizer>();
        builder.Services.AddScoped<TimeZoneService>();

        // Platform-independent command services (used by the bot adapters and, later, the web/API).
        builder.Services.AddScoped<TrackingCommandService>();
        builder.Services.AddScoped<TrackedChannelCommandService>();
        builder.Services.AddScoped<RewardMultiplierCommandService>();
        builder.Services.AddScoped<TrackingPreferenceCommandService>();
        builder.Services.AddScoped<Services.Tracking.ITrackingAuthorizer, Services.Tracking.TrackingAuthorizer>();
        builder.Services.AddScoped<OpCommandService>();
        builder.Services.AddScoped<SeasonCommandService>();
        builder.Services.AddScoped<ConfigCommandService>();
        builder.Services.AddScoped<MemberPreferencesCommandService>();
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
        // Named HttpClient + resilience (the dispatcher resolves it via IHttpClientFactory so the dispatcher itself
        // is a plain scoped registration — Wolverine codegen can constructor-inject it without service location).
        // Like connectors, the secret protector comes from AddMusterConnectorProtection (web + bot only).
        builder.Services.AddHttpClient(Services.Currencies.CurrencyWebhookDispatcher.HttpClientName)
            .AddStandardResilienceHandler();
        builder.Services.AddScoped<Services.Currencies.CurrencyWebhookDispatcher>();
        builder.Services.AddScoped<Services.Currencies.ICurrencyWebhookService, Services.Currencies.CurrencyWebhookService>();

        return builder;
    }

    /// <summary>
    /// Adds Muster's shared readiness health checks: EF DbContext can-connect + Wolverine runtime started.
    /// Both tagged <c>ready</c> so they gate the <c>/health</c> endpoint, not <c>/alive</c> — see
    /// <c>docs/observability.md</c> "Health checks" for the live/ready split + ACA probe semantics.
    ///
    /// <para>Call from web + bot Program.cs (NOT migrations — that host shouldn't expose readiness probes
    /// at all, it's a one-shot job).</para>
    /// </summary>
    public static TBuilder AddMusterSharedHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // EF Core CanConnectAsync against the live SQL connection. Cheap (one round-trip), no schema
            // read — Aspire's auto-added SQL Server check probes the server, this one probes the database
            // (catches "server reachable but our DB credential expired").
            .AddDbContextCheck<MusterDbContext>(
                name: "musterdb",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready"])
            // Wolverine runtime — Started flag flips true after every endpoint + listener + policy is in
            // place. See WolverineRuntimeHealthCheck for why this is intentionally I/O-free.
            .AddCheck<WolverineRuntimeHealthCheck>(
                name: "wolverine",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready"]);

        return builder;
    }

    /// <summary>
    /// Registers Data Protection + the connector secret protector. Call only from hosts that read/write
    /// connector secrets — <b>web and bot</b> — which start after migrations. The migration host omits it.
    ///
    /// <para><b>Publish path</b> (Azure): key ring persisted to the Blob container behind the
    /// <c>ConnectionStrings:blobs</c> URI (Aspire's <c>AddMusterDataProtectionStorage</c>) and each entry
    /// wrapped at rest by the KV RSA key at <c>{ConnectionStrings:kv}/keys/{DataProtection:WrapKeyName}</c>.
    /// Workload identity covers both via <c>DefaultAzureCredential</c> — no secrets in config.</para>
    ///
    /// <para><b>Run path</b> (local): the Azure URIs aren't published in run mode, so falls back to the
    /// previous SQL-backed key ring. Keeps the inner loop offline-friendly.</para>
    ///
    /// <para>App name pinned to <c>Muster</c> so keys are shared across hosts (web ↔ bot must round-trip
    /// the same connector secrets).</para>
    /// </summary>
    public static TBuilder AddMusterConnectorProtection<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddSingleton<Connectors.IConnectorSecretProtector, Connectors.ConnectorSecretProtector>();

        var dp = builder.Services.AddDataProtection().SetApplicationName("Muster");

        // Aspire publishes the DP-keys container URI under ConnectionStrings:dpkeys and the Key Vault URI
        // under ConnectionStrings:kv. Container name + wrap key name come through env from AppHost.
        var containerName = builder.Configuration["DataProtection:Container"];
        var dpContainerUri = builder.Configuration.GetConnectionString(containerName ?? "dpkeys");
        var keyVaultUri = builder.Configuration.GetConnectionString("kv");
        var wrapKeyName = builder.Configuration["DataProtection:WrapKeyName"];

        if (!string.IsNullOrWhiteSpace(dpContainerUri)
            && !string.IsNullOrWhiteSpace(keyVaultUri)
            && !string.IsNullOrWhiteSpace(wrapKeyName))
        {
            // Azure path — Blob for storage, KV for at-rest wrap. DefaultAzureCredential picks up the
            // workload identity in Container Apps (managed identity) and dev cred locally if ever used.
            var credential = new DefaultAzureCredential();

            var containerUri = ResolveBlobContainerUri(dpContainerUri, containerName ?? "dpkeys");
            var keyBlob = new BlobContainerClient(containerUri, credential).GetBlobClient("keys.xml");
            dp.PersistKeysToAzureBlobStorage(keyBlob);

            var keyIdentifier = ResolveKeyIdentifier(keyVaultUri, wrapKeyName);
            dp.ProtectKeysWithAzureKeyVault(keyIdentifier, credential);
        }
        else
        {
            // Local fallback — key ring persisted to SQL. Same shape as the pre-Azure-DP setup.
            dp.PersistKeysToDbContext<MusterDbContext>();
        }

        return builder;
    }

    // Aspire's AzureBlobStorageContainerResource publishes its connection in a key/value form like
    // "Endpoint=https://{acct}.blob.core.windows.net;ContainerName={name}" (or a full storage
    // connection string with AccountName=...;EndpointSuffix=...). Raw new Uri(...) chokes — parse
    // both shapes plus the plain URI form into a usable BlobContainerClient URI.
    private static Uri ResolveBlobContainerUri(string raw, string defaultContainer)
    {
        var trimmed = raw.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var direct)
            && (direct.Scheme == Uri.UriSchemeHttp || direct.Scheme == Uri.UriSchemeHttps))
        {
            return direct;
        }

        var parts = ParseConnectionString(trimmed);

        if (parts.TryGetValue("Endpoint", out var endpoint)
            && Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            var container = parts.GetValueOrDefault("ContainerName", defaultContainer);
            return new Uri($"{endpointUri.GetLeftPart(UriPartial.Authority)}/{container}");
        }

        if (parts.TryGetValue("AccountName", out var account))
        {
            var suffix = parts.GetValueOrDefault("EndpointSuffix", "core.windows.net");
            var container = parts.GetValueOrDefault("ContainerName", defaultContainer);
            return new Uri($"https://{account}.blob.{suffix}/{container}");
        }

        throw new InvalidOperationException(
            $"Could not resolve a blob container URI from ConnectionStrings:dpkeys. Saw: {Mask(trimmed)}");
    }

    // Aspire's AzureKeyVaultResource publishes the vault URI directly, but some integrations wrap it as
    // VaultUri=https://...;... — handle both so the wrap-key URL stitches together either way.
    private static Uri ResolveKeyIdentifier(string raw, string wrapKeyName)
    {
        var trimmed = raw.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var direct)
            && (direct.Scheme == Uri.UriSchemeHttp || direct.Scheme == Uri.UriSchemeHttps))
        {
            return new Uri($"{trimmed.TrimEnd('/')}/keys/{wrapKeyName}");
        }

        var parts = ParseConnectionString(trimmed);
        var vaultUri = parts.GetValueOrDefault("VaultUri") ?? parts.GetValueOrDefault("Endpoint");
        if (!string.IsNullOrWhiteSpace(vaultUri))
        {
            return new Uri($"{vaultUri.TrimEnd('/')}/keys/{wrapKeyName}");
        }

        throw new InvalidOperationException(
            $"Could not resolve a Key Vault URI from ConnectionStrings:kv. Saw: {Mask(trimmed)}");
    }

    private static Dictionary<string, string> ParseConnectionString(string raw) =>
        raw.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);

    // Trim long values + redact anything that looks like a secret (no AccountKey, SharedAccessSignature, etc.
    // ever ends up in our exception text). Just enough info to debug a malformed config without leaking creds.
    private static string Mask(string s) => s.Length > 80 ? s[..80] + "…" : s;
}
