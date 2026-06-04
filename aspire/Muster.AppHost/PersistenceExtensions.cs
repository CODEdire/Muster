using Aspire.Hosting.Azure;
using Azure.Provisioning.Sql;
using Microsoft.Extensions.Configuration;
using Muster.AppHost.Core;
using Muster.AppHost.Options;

namespace Muster.AppHost;

/// <summary>Resource + database names used to wire SQL across the AppHost. Centralised so projects that
/// resolve connection strings by name (e.g. <c>builder.Configuration.GetConnectionString("musterdb")</c>
/// in <c>WolverineExtensions.AddMusterMessaging</c>) and the AppHost itself agree on the same string.</summary>
internal static class PersistenceConstants
{
    /// <summary>Aspire resource id for the Azure SQL Server (logical or container).</summary>
    public const string SqlResourceName = "sql";

    /// <summary>Aspire resource id for the database — the connection-string key every consumer reads
    /// (<c>ConnectionStrings:musterdb</c>). Constant by design; the <i>physical</i> Azure DB name is configurable
    /// via <c>PersistenceOptions.DatabaseName</c>.</summary>
    public const string DatabaseResourceName = "musterdb";

    /// <summary>User-secret parameter name carrying the existing Azure SQL Server's resource name in publish.
    /// CamelCase because azd writes parameters to its .env file as KEY=value and dotenv parsers reject hyphens.</summary>
    public const string SqlServerNameParameter = "sqlServerName";

    /// <summary>User-secret parameter name carrying the existing Azure SQL Server's resource group in publish.</summary>
    public const string SqlResourceGroupParameter = "sqlResourceGroup";
}

/// <summary>
/// AppHost composition for the application's relational store. Pattern matches
/// <c>MessagingExtensions.AddMusterServiceBus</c>: a single entry point that resolves to the right backing
/// per environment.
///
/// <para><b>Run mode</b> (local <c>dotnet run</c>): a SQL Server container with a named data volume and a
/// persistent container lifetime, so the database survives <c>dotnet run</c> restarts and dev data isn't
/// thrown away every iteration.</para>
///
/// <para><b>Publish mode</b> (azd / Container Apps deploy): shaped by <see cref="PersistenceOptions"/>. By default
/// Aspire provisions a new Azure SQL Server; set <c>UseExisting=true</c> to bind a pre-provisioned server via
/// <c>AsExisting(...)</c> (server name + RG from the AppHost parameters
/// <see cref="PersistenceConstants.SqlServerNameParameter"/> / <see cref="PersistenceConstants.SqlResourceGroupParameter"/>).
/// The database is always authored by Aspire — SKU (default Basic / 5 DTU) and backup storage redundancy
/// (default Zone) come from the options.</para>
///
/// <para><b>Auth in publish</b>: Aspire's Azure SQL integration uses Microsoft.Data.SqlClient with Entra access
/// tokens — no SQL password in config. When Aspire provisions the server it auto-emits the deployment script that
/// creates the workload-identity SQL user + grants <c>db_owner</c>. With <c>UseExisting=true</c> that script is
/// skipped — the SQL user is a manual one-time-per-env step (see <c>docs/deployment.md</c> "Passwordless SQL").</para>
/// </summary>
internal static class PersistenceExtensions
{
    /// <summary>Platform step: adds the SQL server (container locally; provisioned or existing Azure SQL in publish)
    /// and the application database (SKU + backup storage redundancy from <see cref="PersistenceOptions"/>), stashing
    /// the database builder on the platform.</summary>
    public static MusterPlatformBuilder AddPersistence(this MusterPlatformBuilder p)
    {
        var builder = p.Inner;
        var config = builder.Configuration.GetSection(nameof(PersistenceOptions)).Get<PersistenceOptions>();

        var sql = builder.AddAzureSqlServer(PersistenceConstants.SqlResourceName);

        if (builder.ExecutionContext.IsRunMode)
        {
            // Local container with persistence across runs.
            sql.RunAsContainer(container => container
                .WithDataVolume()
                .WithLifetime(ContainerLifetime.Persistent));
        }
        else
        {
            // Bind an existing server when configured; otherwise Aspire provisions a fresh one (and auto-emits the
            // workload-identity SQL user grant). The server name + RG come from AppHost parameters (user-secrets /
            // KV refs in the deploy env).
            if (config?.UseExisting == true)
            {
                sql.AsExisting(
                    builder.AddParameter(PersistenceConstants.SqlServerNameParameter),
                    builder.AddParameter(PersistenceConstants.SqlResourceGroupParameter));
            }

            // Configure the database Aspire authors (SKU tier/DTU + backup storage redundancy). Applies to the new
            // DB even when the SERVER is bound AsExisting — the database resource is still ours.
            sql.ConfigureInfrastructure(infrastructure =>
            {
                var sqlDb = infrastructure.GetProvisionableResources()
                    .OfType<SqlDatabase>()
                    .Single();

                if (!sqlDb.IsExistingResource && config is not null)
                {
                    // Aspire's AddAzureSqlServer defaults the database to the Azure SQL "free offer"
                    // (UseFreeLimit=true + a serverless GP SKU). That's incompatible with our paid SKU and isn't
                    // even supported in every region/SLO — Azure rejects it with ProvisioningDisabled. Turn the free
                    // flag off and clear the exhaustion behavior before applying the configured (Basic/DTU) SKU.
                    sqlDb.UseFreeLimit = false;
                    sqlDb.FreeLimitExhaustionBehavior = null!; // clears Aspire's free-offer default; non-nullable BicepValue, hence null!
                    sqlDb.Sku = new SqlSku { Name = config.SkuName, Tier = config.SkuTier, Capacity = config.SkuCapacity };
                    sqlDb.RequestedBackupStorageRedundancy = config.BackupStorageRedundancy;
                }
            });
        }

        p.Database = sql.WithMusterDb(config?.DatabaseName ?? "musterdb");
        return p;
    }

    /// <summary>Adds the application database to the SQL server: the Aspire resource id (connection-string key)
    /// stays the constant <see cref="PersistenceConstants.DatabaseResourceName"/>; <paramref name="databaseName"/>
    /// is the physical Azure DB name. Returns the database builder for <c>WithReference(db)</c> wiring.</summary>
    public static IResourceBuilder<AzureSqlDatabaseResource> WithMusterDb(
        this IResourceBuilder<AzureSqlServerResource> sql, string databaseName)
        => sql.AddDatabase(PersistenceConstants.DatabaseResourceName, databaseName);
}
