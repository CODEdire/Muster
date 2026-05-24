using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;

namespace Muster.Infrastructure;

public static class WolverineExtensions
{
    /// <summary>
    /// Configures the Wolverine command/query bus and discovers handlers in the Infrastructure assembly.
    /// When a SQL connection is available it enables the durable outbox/inbox (EF Core + SQL Server) so
    /// ledger writes and the messages they cascade commit together; otherwise it runs in-memory (local
    /// dev without a database). No external broker in v1 — contracts are broker-agnostic so Azure Service
    /// Bus can be enabled later without handler changes.
    /// </summary>
    public static TBuilder AddMusterMessaging<TBuilder>(this TBuilder builder, string connectionName = "musterdb")
        where TBuilder : IHostApplicationBuilder
    {
        var connectionString = builder.Configuration.GetConnectionString(connectionName);

        // AddMusterMessaging lives in Infrastructure, so Wolverine would otherwise infer the application
        // assembly as Infrastructure and miss the host's Wolverine.HTTP endpoints (in Muster.Web). Point
        // it at the host's entry assembly and explicitly include Infrastructure for message handlers.
        var hostAssembly = Assembly.GetEntryAssembly() ?? typeof(WolverineExtensions).Assembly;

        builder.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = hostAssembly;
            opts.Discovery.IncludeAssembly(hostAssembly);
            opts.Discovery.IncludeAssembly(typeof(WolverineExtensions).Assembly);

            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                opts.PersistMessagesWithSqlServer(connectionString, "muster");
                opts.UseEntityFrameworkCoreTransactions();
            }
        });

        return builder;
    }
}
