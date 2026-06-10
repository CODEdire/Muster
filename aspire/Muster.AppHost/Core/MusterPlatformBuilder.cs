using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.AppContainers;

namespace Muster.AppHost.Core;

/// <summary>
/// Fluent assembler for the Muster platform graph. State only: it holds the inner
/// <see cref="IDistributedApplicationBuilder"/> and the resource builders accumulated as the chain runs. The
/// chain steps themselves (<c>AddPersistence</c>, <c>AddKeyVault</c>, …) are extension methods that live next to
/// each resource's provisioning logic in its <c>*Extensions</c> file and assign the matching property here, so
/// <c>AppHost.cs</c> reads as <c>builder.AddMuster().AddPersistence().AddMessaging()…​.Build()</c>.
///
/// <para>Call order follows the dependency graph: <c>AddLogAnalytics</c> before <c>AddContainerEnvironment</c> /
/// <c>AddApplicationInsights</c>; <c>AddPersistence</c> (and <c>AddApplicationInsights</c> for telemetry) before
/// <c>AddMigrations</c>. <see cref="Build"/> snapshots the subset web + bot consume into a <see cref="MusterPlatform"/>.</para>
///
/// <para>Publish-only resources (Key Vault, App Configuration, App Insights, Log Analytics, the container
/// env/registry) stay null in run mode where their factories no-op.</para>
/// </summary>
internal sealed class MusterPlatformBuilder(IDistributedApplicationBuilder builder)
{
    /// <summary>The underlying Aspire builder — the chain steps call <c>AddAzure*</c> through this.</summary>
    internal IDistributedApplicationBuilder Inner => builder;

    public IResourceBuilder<AzureSqlDatabaseResource>? Database { get; internal set; }
    public IResourceBuilder<AzureServiceBusResource>? Messaging { get; internal set; }
    public IResourceBuilder<AzureLogAnalyticsWorkspaceResource>? LogAnalytics { get; internal set; }
    public IResourceBuilder<AzureContainerRegistryResource>? ContainerRegistry { get; internal set; }
    public IResourceBuilder<AzureContainerAppEnvironmentResource>? ContainerEnvironment { get; internal set; }
    public IResourceBuilder<AzureKeyVaultResource>? KeyVault { get; internal set; }
    public IResourceBuilder<AzureAppConfigurationResource>? AppConfiguration { get; internal set; }
    public IResourceBuilder<AzureStorageResource>? Storage { get; internal set; }
    public IResourceBuilder<AzureBlobStorageContainerResource>? DataProtectionKeys { get; internal set; }
    public IResourceBuilder<AzureBlobStorageContainerResource>? ShopImages { get; internal set; }
    public IResourceBuilder<AzureApplicationInsightsResource>? ApplicationInsights { get; internal set; }
    public IResourceBuilder<AzureSignalRResource>? SignalR { get; internal set; }
    public IResourceBuilder<ProjectResource>? Migrations { get; internal set; }
    public IResourceBuilder<ParameterResource>? DiscordToken { get; internal set; }
    public IResourceBuilder<ParameterResource>? DiscordClientId { get; internal set; }
    public IResourceBuilder<ParameterResource>? DiscordClientSecret { get; internal set; }

    /// <summary>Discord credential parameters (user-secrets locally, Key Vault refs in publish). No provisioning,
    /// so it lives on the builder rather than in a resource <c>*Extensions</c> file.</summary>
    public MusterPlatformBuilder AddDiscord()
    {
        DiscordToken = builder.AddParameter("discordToken", secret: true);
        DiscordClientId = builder.AddParameter("discordClientId", secret: true);
        DiscordClientSecret = builder.AddParameter("discordClientSecret", secret: true);
        return this;
    }

    /// <summary>Snapshots the references web + bot consume into an immutable <see cref="MusterPlatform"/>. Throws
    /// if a required resource hasn't been added yet.</summary>
    public MusterPlatform Build() => new()
    {
        Database = Required(Database, "AddPersistence"),
        Messaging = Required(Messaging, "AddMessaging"),
        Storage = Required(Storage, "AddStorage"),
        DataProtectionKeys = Required(DataProtectionKeys, "AddStorage"),
        ShopImages = Required(ShopImages, "AddStorage"),
        Migrations = Required(Migrations, "AddMigrations"),
        KeyVault = KeyVault,
        AppConfiguration = AppConfiguration,
        ApplicationInsights = ApplicationInsights,
        SignalR = SignalR,
        DiscordToken = Required(DiscordToken, nameof(AddDiscord)),
        DiscordClientId = Required(DiscordClientId, nameof(AddDiscord)),
        DiscordClientSecret = Required(DiscordClientSecret, nameof(AddDiscord)),
    };

    private static IResourceBuilder<T> Required<T>(IResourceBuilder<T>? resource, string addMethod) where T : IResource
        => resource ?? throw new InvalidOperationException($"Platform is missing a required resource — call {addMethod}() before Build().");
}

internal static class MusterPlatformBuilderExtensions
{
    /// <summary>Entry point for the fluent platform assembly: <c>builder.AddMuster().AddPersistence()…​.Build()</c>.</summary>
    public static MusterPlatformBuilder AddMuster(this IDistributedApplicationBuilder builder) => new(builder);
}
