using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Muster.AppHost.Core;

namespace Muster.AppHost.PlatformExtensions;

/// <summary>
/// Aggregate wiring for a compute project (web, bot). One call binds every shared platform reference and its
/// RBAC role assignment, so the <c>Add*Host</c> extensions stay focused on project-specific shape
/// (endpoints, probes, scaling) instead of repeating the reference graph.
/// </summary>
internal static class PlatformWiringExtensions
{
    /// <summary>Wires the full platform onto a project: SQL, Service Bus, Data Protection (Blob + KV wrap key
    /// env), Key Vault, App Configuration, App Insights, plus the migration-job completion gate. Publish-only
    /// resources (Key Vault, App Configuration, App Insights) are skipped in run mode where they're null.</summary>
    public static IResourceBuilder<T> WithMusterPlatform<T>(this IResourceBuilder<T> consumer, MusterPlatform platform)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        consumer
            .WithReference(platform.Database)
            .WithMusterMessaging(platform.Messaging)
            .WithMusterDataProtection(platform.Storage, platform.DataProtectionKeys)
            .WaitForCompletion(platform.Migrations);

        if (platform.KeyVault is not null)
        {
            consumer.WithMusterKeyVault(platform.KeyVault);
        }

        if (platform.AppConfiguration is not null)
        {
            consumer.WithMusterAppConfiguration(platform.AppConfiguration);
        }

        if (platform.ApplicationInsights is not null)
        {
            consumer.WithMusterApplicationInsights(platform.ApplicationInsights);
        }

        return consumer;
    }
}
