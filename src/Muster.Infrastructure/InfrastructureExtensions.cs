using Microsoft.Extensions.Hosting;

namespace Muster.Infrastructure;

public static class InfrastructureExtensions
{
    /// <summary>
    /// Registers <see cref="MusterDbContext"/> against the Aspire-provided connection. In Azure the
    /// connection string carries no password (Entra managed identity / Active Directory Default).
    /// </summary>
    public static TBuilder AddMusterInfrastructure<TBuilder>(this TBuilder builder, string connectionName = "musterdb")
        where TBuilder : IHostApplicationBuilder
    {
        builder.AddSqlServerDbContext<MusterDbContext>(connectionName);
        return builder;
    }
}
