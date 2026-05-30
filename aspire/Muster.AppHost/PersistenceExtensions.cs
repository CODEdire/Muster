using Aspire.Hosting.Azure;

namespace Muster.AppHost;

/// <summary>Resource + database names used to wire SQL across the AppHost. Centralised so projects that
/// resolve connection strings by name (e.g. <c>builder.Configuration.GetConnectionString("musterdb")</c>
/// in <c>WolverineExtensions.AddMusterMessaging</c>) and the AppHost itself agree on the same string.</summary>
internal static class PersistenceConstants
{
    /// <summary>Aspire resource id for the Azure SQL Server (logical or container).</summary>
    public const string SqlResourceName = "sql";

    /// <summary>Aspire resource id AND the database name on the server. Matches the connection-string key
    /// every consumer reads (<c>ConnectionStrings:musterdb</c>).</summary>
    public const string DatabaseResourceName = "musterdb";

    /// <summary>User-secret parameter name carrying the existing Azure SQL Server's resource name in publish.</summary>
    public const string SqlServerNameParameter = "sql-server-name";

    /// <summary>User-secret parameter name carrying the existing Azure SQL Server's resource group in publish.</summary>
    public const string SqlResourceGroupParameter = "sql-resource-group";
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
/// <para><b>Publish mode</b> (azd / Container Apps deploy): bound via <c>AsExisting(...)</c> to a
/// pre-provisioned Azure SQL Server in your subscription. The server name + resource group come from two
/// AppHost parameters (<see cref="PersistenceConstants.SqlServerNameParameter"/>,
/// <see cref="PersistenceConstants.SqlResourceGroupParameter"/>) — set via <c>dotnet user-secrets</c> on
/// the AppHost project, or via Key Vault refs once the deploy environment is wired up. The database
/// (<see cref="PersistenceConstants.DatabaseResourceName"/>) is assumed to already exist on that server;
/// the MigrationService runs the schema bootstrap on first deploy.</para>
///
/// <para><b>Auth in publish</b>: Aspire's Azure SQL integration uses Microsoft.Data.SqlClient with Entra
/// access tokens — no SQL password in config. When Aspire fully provisions the server it also emits a
/// deployment script that creates the workload-identity SQL user + grants <c>db_owner</c>. When bound via
/// <c>AsExisting</c> (our case), that script is skipped — the SQL user must be created manually one time
/// per environment. See <c>docs/deployment.md</c> "Passwordless SQL" for the exact runbook.</para>
/// </summary>
internal static class PersistenceExtensions
{
    /// <summary>Adds the SQL server resource and the application database. Returns the database builder so
    /// consumers chain <c>WithReference(db)</c>.</summary>
    public static IResourceBuilder<AzureSqlServerResource> AddMusterPersistence(this IDistributedApplicationBuilder builder)
    {
        var sql = builder.AddAzureSqlServer(PersistenceConstants.SqlResourceName);

        if (builder.ExecutionContext.IsRunMode)
        {
            // Local container with persistence across runs — same shape as the previous inline setup.
            sql.RunAsContainer(container => container
                .WithDataVolume()
                .WithLifetime(ContainerLifetime.Persistent));
        }
        else
        {
            // Publish: bind to the pre-provisioned Azure SQL Server. Parameters are set via user-secrets on
            // the AppHost project (`dotnet user-secrets set Parameters:sql-server-name <server>`), and via
            // Key Vault refs in the deploy environment.
            var serverName = builder.AddParameter(PersistenceConstants.SqlServerNameParameter);
            var resourceGroup = builder.AddParameter(PersistenceConstants.SqlResourceGroupParameter);
            sql.AsExisting(serverName, resourceGroup);
        }

        return sql;
    }


    public static IResourceBuilder<AzureSqlDatabaseResource> WithMusterDb(this IResourceBuilder<AzureSqlServerResource> sql)
        => sql.AddDatabase(PersistenceConstants.DatabaseResourceName);
}
