using Aspire.Hosting.Azure;
using Azure.Provisioning.AppContainers;

namespace Muster.AppHost;

/// <summary>
/// AppHost composition for the Discord bot. Mirrors the per-feature extension pattern.
///
/// <para><b>Run mode</b>: standard Aspire-managed dotnet process; gateway connects on startup,
/// in-process bot stays attached until the run ends.</para>
///
/// <para><b>Publish mode</b>: emitted as a Container App pinned to a single replica
/// (<c>MinReplicas = MaxReplicas = 1</c>) because the Discord gateway protocol allows exactly one
/// session per token per shard — a second replica would have its identify rejected and double-process
/// events in the brief overlap. See <c>docs/deployment.md</c> "Bot drain — the gateway singleton
/// problem" for the full rationale + how SIGTERM lets in-flight Wolverine + Discord REST handlers
/// complete cleanly within the 60s grace.</para>
///
/// <para>Resource sizing is intentionally small (0.25 vCPU / 0.5 GiB): the bot is mostly idle waiting
/// for gateway events; spikes are short. Raise if a single shard ever struggles, but the right answer
/// for sustained pressure is sharding, not vertical scale.</para>
/// </summary>
internal static class BotHostingExtensions
{
    public const string ResourceName = "bot";

    public static IResourceBuilder<ProjectResource> AddMusterBot(
        this IDistributedApplicationBuilder builder,
        IResourceBuilder<AzureSqlDatabaseResource> db,
        IResourceBuilder<AzureServiceBusResource> messaging,
        IResourceBuilder<ParameterResource> discordToken,
        IResourceBuilder<ProjectResource> migrations,
        IResourceBuilder<AzureKeyVaultResource>? keyVault,
        IResourceBuilder<AzureAppConfigurationResource>? appConfig,
        IResourceBuilder<AzureBlobStorageContainerResource> dpKeys,
        IResourceBuilder<AzureApplicationInsightsResource>? appInsights,
        EndpointReference? webBaseUrl = null)
    {
        var bot = builder.AddProject<Projects.Muster_Bot>(ResourceName)
            .WithReference(db)
            .WithMusterMessaging(messaging)
            .WithReference(dpKeys)
            .WithEnvironment("Discord__Token", discordToken)
            // Data Protection wiring — both URIs are read by Infrastructure.AddMusterConnectorProtection
            // to decide between Azure DP (Blob + KV wrap) and the local SQL fallback.
            .WithEnvironment("DataProtection__WrapKeyName", KeyVaultConstants.DataProtectionWrapKeyName)
            .WithEnvironment("DataProtection__Container", StorageConstants.DataProtectionContainerName)
            .WaitForCompletion(migrations);

        if (keyVault is not null)
        {
            bot.WithReference(keyVault);
        }
        if (appConfig is not null)
        {
            bot.WithReference(appConfig);
        }
        // App Insights ref publishes APPLICATIONINSIGHTS_CONNECTION_STRING which UseAzureMonitor() in
        // ServiceDefaults picks up. Null in run mode → telemetry stays on the Aspire Dashboard via OTLP.
        if (appInsights is not null)
        {
            bot.WithReference(appInsights);
        }

        if (webBaseUrl is not null)
        {
            // Public URL the quest-board embeds use for tier/guild icon image hosts + deep links.
            bot.WithEnvironment("Web__BaseUrl", webBaseUrl);
        }

        if (builder.ExecutionContext.IsPublishMode)
        {
            bot.PublishAsAzureContainerApp((_, app) =>
            {
                var container = app.Template.Containers[0].Value!;
                container.Resources.Cpu = 0.25;
                container.Resources.Memory = "0.5Gi";

                // Gateway singleton — second replica's identify gets rejected by Discord; also racy on
                // currency awards / quest board renders during overlap. Pinned at 1 forever.
                app.Template.Scale.MinReplicas = 1;
                app.Template.Scale.MaxReplicas = 1;

                // 60s grace lets the longest in-flight handler (Discord REST quest-board update,
                // Wolverine handler) finish before SIGKILL.
                app.Template.TerminationGracePeriodSeconds = 60;

                // Single revision: new replica spins up, identifies, Discord boots old session,
                // brief no-event window, new replica takes over.
                app.Configuration.ActiveRevisionsMode = ContainerAppActiveRevisionsMode.Single;
            });
        }

        return bot;
    }
}
