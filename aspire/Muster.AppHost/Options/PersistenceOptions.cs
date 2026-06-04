using Azure.Provisioning.Sql;

namespace Muster.AppHost.Options;

public record PersistenceOptions
{
    /// <summary>True = bind an existing Azure SQL Server (via AsExisting + the server/RG parameters). False
    /// (default) = let Aspire provision a new server — which also auto-emits the workload-identity SQL user grant.
    /// The database itself is always authored by Aspire (and configured per the Sku/backup settings below).</summary>
    public bool UseExisting { get; init; } = false;

    /// <summary>Actual Azure database name. The Aspire reference id (connection-string key) stays a constant —
    /// see <c>PersistenceConstants.DatabaseResourceName</c>; only the physical DB name is configurable here.</summary>
    public string DatabaseName { get; init; } = "musterdb";

    // Database SKU. Default = Basic tier (5 DTU).
    public string SkuName { get; init; } = "Basic";

    public string SkuTier { get; init; } = "Basic";

    public int SkuCapacity { get; init; } = 5;

    /// <summary>Backup storage redundancy. Default Zone (ZRS).</summary>
    public SqlBackupStorageRedundancy BackupStorageRedundancy { get; init; } = SqlBackupStorageRedundancy.Zone;
}
