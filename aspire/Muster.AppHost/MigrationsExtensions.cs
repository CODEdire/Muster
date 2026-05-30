using Aspire.Hosting.Azure;
using Azure.Provisioning.AppContainers;

namespace Muster.AppHost;

/// <summary>
/// AppHost composition for the run-once schema migration job. Mirrors the per-feature extension pattern
/// (<c>MessagingExtensions</c>, <c>PersistenceExtensions</c>): one entry point, environment-aware shape.
///
/// <para><b>Run mode</b> (local <c>dotnet run</c>): runs as a normal Aspire-managed process. Bot + web gate
/// on its completion via <c>WaitForCompletion(migrations)</c> in <c>AppHost.cs</c>.</para>
///
/// <para><b>Publish mode</b> (azd / Container Apps): emitted as a <b>Container App Job</b> with a Manual
/// trigger via <c>PublishAsAzureContainerAppJob</c> (Aspire's built-in helper). A long-running Container
/// App would auto-restart the migrator after exit, which is wrong for a one-shot. The deploy pipeline
/// starts the job, waits for completion, then rolls new bot/web revisions — see <c>docs/deployment.md</c>
/// "Migration job orchestration".</para>
/// </summary>
internal static class MigrationsExtensions
{
    /// <summary>Aspire resource id for the migration job. Other projects reference it by this name in
    /// <c>WaitForCompletion(...)</c>.</summary>
    public const string ResourceName = "migrations";

    /// <summary>Adds the migration service project, wires it to the database, and in publish-mode flips it
    /// from "Container App" to "Container App Job" so it runs to completion instead of being restarted.</summary>
    public static IResourceBuilder<ProjectResource> AddMusterMigrations(
        this IDistributedApplicationBuilder builder,
        IResourceBuilder<AzureSqlDatabaseResource> db,
        IResourceBuilder<AzureApplicationInsightsResource>? appInsights = null)
    {
        var migrations = builder.AddProject<Projects.Muster_MigrationService>(ResourceName)
            .WithReference(db)
            .WaitFor(db);

        // App Insights ref in publish so the migration job's telemetry (EF Core migrations, SQL traces,
        // exit code) lands in the same workspace as web/bot. Null in run mode → Aspire Dashboard via OTLP.
        if (appInsights is not null)
        {
            migrations.WithReference(appInsights);
        }

        if (builder.ExecutionContext.IsPublishMode)
        {
#pragma warning disable ASPIREAZURE002 // PublishAsAzureContainerAppJob is [Experimental] in Aspire 13.3.5;
            // when it stabilises the suppression drops out, the call stays identical.
            migrations.PublishAsAzureContainerAppJob((_, job) =>
            {
                job.Configuration.TriggerType = ContainerAppJobTriggerType.Manual;
                // 1-hour cap (Container Apps Jobs max is 24h; our schema bootstrap should finish in seconds).
                job.Configuration.ReplicaTimeout = (int)TimeSpan.FromHours(1).TotalSeconds;
                // Schema migrations are not safely retryable mid-run — fail fast, let the pipeline rerun.
                job.Configuration.ReplicaRetryLimit = 0;
            });
#pragma warning restore ASPIREAZURE002
        }

        return migrations;
    }
}
