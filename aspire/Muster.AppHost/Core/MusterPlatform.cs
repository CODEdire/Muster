using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;

namespace Muster.AppHost.Core;

/// <summary>
/// The shared platform resources every Muster compute project (web, bot) binds to. Assembled once in
/// <c>AppHost.cs</c> and threaded into the <c>Add*Host</c> extensions as a single argument, so the long
/// per-project reference lists collapse and the actual wiring — references + RBAC role assignments —
/// lives in one place (<see cref="PlatformExtensions.PlatformWiringExtensions.WithMusterPlatform{T}"/>).
///
/// <para>Nullable members are publish-only resources whose <c>AddMuster*</c> factories return <c>null</c> in
/// run mode; <c>WithMusterPlatform</c> skips them locally.</para>
/// </summary>
internal sealed record MusterPlatform
{
    public required IResourceBuilder<AzureSqlDatabaseResource> Database { get; init; }
    public required IResourceBuilder<AzureServiceBusResource> Messaging { get; init; }
    public required IResourceBuilder<AzureStorageResource> Storage { get; init; }
    public required IResourceBuilder<AzureBlobStorageContainerResource> DataProtectionKeys { get; init; }

    /// <summary>Blob container for shop listing/storefront images (web-only consumer).</summary>
    public required IResourceBuilder<AzureBlobStorageContainerResource> ShopImages { get; init; }

    public required IResourceBuilder<ProjectResource> Migrations { get; init; }

    public IResourceBuilder<AzureKeyVaultResource>? KeyVault { get; init; }
    public IResourceBuilder<AzureAppConfigurationResource>? AppConfiguration { get; init; }
    public IResourceBuilder<AzureApplicationInsightsResource>? ApplicationInsights { get; init; }

    /// <summary>Web-only — the Blazor circuit backplane. Bot doesn't use SignalR, so it's wired in AddMusterWeb
    /// rather than the shared <c>WithMusterPlatform</c>.</summary>
    public IResourceBuilder<AzureSignalRResource>? SignalR { get; init; }

    public required IResourceBuilder<ParameterResource> DiscordToken { get; init; }
    public required IResourceBuilder<ParameterResource> DiscordClientId { get; init; }
    public required IResourceBuilder<ParameterResource> DiscordClientSecret { get; init; }
}
