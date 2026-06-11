using System.Reflection;
using Azure.Identity;
using JasperFx;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Muster.Contracts;
using Muster.Infrastructure.Messaging;
using Wolverine;
using Wolverine.AzureServiceBus;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;

namespace Muster.Infrastructure;

public static class WolverineExtensions
{
    /// <summary>Global prefix Wolverine applies to every queue/topic identifier on the Service Bus
    /// transport (via <c>PrefixIdentifiers</c>). Same value the AppHost uses when declaring topology,
    /// so the two sides resolve to identical resource names (<c>muster.&lt;contract-name&gt;</c>).</summary>
    public const string TransportPrefix = "muster";

    /// <summary>
    /// Configures the Wolverine command/query bus and discovers handlers in the Infrastructure assembly +
    /// the host entry assembly. Persistence stays on SQL Server (EF Core outbox/inbox) so ledger writes and
    /// the messages they cascade commit together. Cross-host messages travel over Azure Service Bus:
    /// emulator connection string locally (Aspire publishes <c>ConnectionStrings:messaging</c>), live namespace
    /// + <see cref="DefaultAzureCredential"/> in Staging/Production (Aspire publishes
    /// <c>Azure:ServiceBus:FullyQualifiedNamespace</c>). With neither set the bus runs in-memory (local dev
    /// without a database, integration tests).
    ///
    /// Routing is <b>explicit</b> from <see cref="MessageRouting.Contracts"/>: each entry becomes a
    /// <c>PublishMessage&lt;T&gt;().ToAzureServiceBusTopic(...)</c> rule plus, if this host appears in the
    /// entry's <c>SubscribingHosts</c>, a matching listener on <paramref name="hostName"/>. Local commands
    /// (<c>IGuildCommand</c> et al.) are never claimed by the transport — they flow straight to their
    /// in-process handlers discovered in the Infrastructure / host assemblies.
    ///
    /// Wolverine owns all topology provisioning via <c>AutoProvision()</c> — application topics + per-host
    /// subscriptions + its own operational queues (per-node response, per-host retry, shared dead-letter).
    /// Against the live namespace this needs the workload identity to hold <b>Azure Service Bus Data
    /// Owner</b>; against the emulator Wolverine reaches the management plane via the separate management
    /// connection string the AppHost wires through (<c>ConnectionStrings:messaging-management</c>, built
    /// from the emulator's HTTP management endpoint on port 5300).
    /// </summary>
    /// <remarks>No host auto-provisions the SQL message-store schema (<c>muster.wolverine_*</c>) — see the SQL
    /// persistence block below. The schema is managed out of band via exported scripts / the JasperFx db-* CLI;
    /// see <c>docs/deployment.md</c> "Wolverine message-store schema".</remarks>
    public static TBuilder AddMusterMessaging<TBuilder>(this TBuilder builder, string hostName, string connectionName = "musterdb", string messagingConnectionName = "messaging")
        where TBuilder : IHostApplicationBuilder
    {
        var sqlConnectionString = builder.Configuration.GetConnectionString(connectionName);
        var rawMessaging = builder.Configuration.GetConnectionString(messagingConnectionName);
        var serviceBusFqdn = builder.Configuration["Azure:ServiceBus:FullyQualifiedNamespace"];

        // Aspire publishes the same ConnectionStrings:messaging key for BOTH the emulator (full SAS
        // conn string with SharedAccessKey=...) and a live namespace (just the FQDN —
        // token-credential auth). Distinguish by the SAS marker; the FQDN form has no SharedAccessKey
        // and trips Wolverine's ServiceBusConnectionStringProperties.Parse when handed to the
        // emulator-shaped UseAzureServiceBus(connStr) overload.
        var hasSasMarker = !string.IsNullOrWhiteSpace(rawMessaging)
            && rawMessaging.Contains("SharedAccessKey", StringComparison.OrdinalIgnoreCase);
        var serviceBusConnectionString = hasSasMarker ? rawMessaging : null;
        if (string.IsNullOrWhiteSpace(serviceBusFqdn) && !hasSasMarker)
        {
            serviceBusFqdn = rawMessaging; // FQDN published under ConnectionStrings:messaging
        }

        // AddMusterMessaging lives in Infrastructure, so Wolverine would otherwise infer the application
        // assembly as Infrastructure and miss the host's Wolverine.HTTP endpoints (in Muster.Web). Point
        // it at the host's entry assembly and explicitly include Infrastructure for message handlers.
        var hostAssembly = Assembly.GetEntryAssembly() ?? typeof(WolverineExtensions).Assembly;

        builder.UseWolverine(opts =>
        {
            if (!string.IsNullOrWhiteSpace(hostName))
            {
                opts.ServiceName = hostName;
            }

            opts.ApplicationAssembly = hostAssembly;
            opts.Discovery.IncludeAssembly(hostAssembly);
            opts.Discovery.IncludeAssembly(typeof(WolverineExtensions).Assembly);

            // Wolverine's default optimization: when a host has both a publish rule AND a local handler
            // for the same message, it short-circuits to local in-process dispatch and SKIPS the transport.
            // That breaks cross-host fan-out — e.g. web publishing QuestLifecycleNotified would handle it
            // locally and the message would never reach the bot's subscription. Disabling the convention
            // forces every message with a transport route to actually traverse the transport, so each host's
            // own subscription delivers to its handler the same way the other hosts' subs do.
            opts.Policies.DisableConventionalLocalRouting();

            // …but that also strips the automatic local route from local-only background commands that are
            // *published* (fire-and-forget) and never cross a host — without one, PublishAsync throws
            // "No routes can be determined for …". These two aren't in the cross-host manifest, so re-add an
            // explicit local route for each so they reach their in-process handler. The queue is durable when a
            // message store is configured, so a web restart doesn't drop an in-flight sync/bulk run.
            opts.PublishMessage<SyncCurrencyBalances>().ToLocalQueue("background");
            opts.PublishMessage<RunCurrencyBulkAdjust>().ToLocalQueue("background");
            if (!string.IsNullOrWhiteSpace(sqlConnectionString))
            {
                opts.LocalQueue("background").UseDurableInbox();
            }

            // Audit every successful guild command in one place — attached only to IGuildCommand handler chains.
            opts.Policies.AddMiddleware(typeof(AuditMiddleware),
                chain => typeof(Muster.Contracts.IGuildCommand).IsAssignableFrom(chain.MessageType));

            // Diagnostic: log receive-side activity for every cross-host message so the bot/web consoles
            // show RECV/DONE/FAIL lines for QuestLifecycleNotified et al. Attached only to chains whose
            // message type appears in the cross-host manifest — local commands stay quiet.
            opts.Policies.AddMiddleware(typeof(MessageFlowMiddleware),
                chain => MessageRouting.IsCrossHost(chain.MessageType));

            // EF integration is always on so Wolverine can inline the DbContext into handler codegen (and
            // enlist SaveChanges in the outbox).
            opts.UseEntityFrameworkCoreTransactions();

            // SQL Server is the durable message store (outbox/inbox) — required for transactional sends. Without
            // it the bus runs in-memory (local dev / tests); cross-host messages are skipped in that mode too.
            if (!string.IsNullOrWhiteSpace(sqlConnectionString))
            {
                opts.PersistMessagesWithSqlServer(sqlConnectionString, "muster");

                // NO host auto-provisions the muster.wolverine_* schema. Auto-build on startup made every replica
                // race the same DDL (and raced EF's migration), hanging deploys on Sch-M lock waits → SqlException
                // -2. The schema is created/patched explicitly, out of band, via exported SQL scripts and the
                // JasperFx db-* CLI (db-dump / db-apply / db-assert). See docs/deployment.md "Wolverine
                // message-store schema". The migration host exposes those commands (RunJasperFxCommands).
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            }

            // Pick the Service Bus transport: emulator connection string takes priority (local), then live
            // namespace FQDN with managed identity (Staging/Production). With neither present the bus is purely
            // in-memory — exactly what the integration tests rely on.
            var useEmulator = !string.IsNullOrWhiteSpace(serviceBusConnectionString);
            var useLive = !useEmulator && !string.IsNullOrWhiteSpace(serviceBusFqdn);
            if (!useEmulator && !useLive)
            {
                return;
            }

            var bus = useEmulator
                ? opts.UseAzureServiceBus(serviceBusConnectionString!)
                : opts.UseAzureServiceBus(serviceBusFqdn!, new DefaultAzureCredential());

            bus.PrefixIdentifiers(TransportPrefix);

            // Wolverine owns all topology provisioning. Against the live namespace this needs Data Owner
            // RBAC; against the emulator it needs the separate management connection string the AppHost
            // wires through (the emulator splits AMQP and the admin API onto different ports).
            var managementConnectionString = builder.Configuration.GetConnectionString("messaging-management");
            if (!string.IsNullOrWhiteSpace(managementConnectionString))
            {
                var transport = opts.Transports.GetOrCreate<AzureServiceBusTransport>();
                transport.ManagementConnectionString = managementConnectionString;
            }

            bus.AutoProvision();

            // Conventional topic + subscription routing. Topic name derived from the contract type via the
            // shared MessageRouting.TopicName algorithm (kebab-case of the CLR name); subscription name is
            // the host's identity (so a topic with two listening hosts shows two clearly named subs).
            // IncludeTypes scopes the convention to the cross-host manifest only — without it Wolverine
            // would auto-route every discovered message type (every local IGuildCommand, every NetCord
            // gateway event) to a remote topic.
            bus.UseTopicAndSubscriptionConventionalRouting(c =>
            {
                c.IncludeTypes(MessageRouting.IsCrossHost);
                c.TopicNameForSender(MessageRouting.TopicName);
                c.TopicNameForListener(MessageRouting.TopicName);
                c.SubscriptionNameForListener(_ => hostName);
            });
        });

        return builder;
    }
}
