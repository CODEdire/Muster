using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;

namespace Muster.AppHost;

/// <summary>
/// AppHost composition for the Azure Service Bus namespace that backs Wolverine's cross-host transport.
/// Topology (topics + per-host subscriptions + the shared dead-letter queue + Wolverine's per-node
/// response queue + per-host retry queue) is owned by Wolverine via <c>AutoProvision()</c>. The AppHost
/// only stands up the namespace and, in run mode, the emulator container with both AMQP (5672) and HTTP
/// management (5300) ports exposed so Wolverine's admin client can reach the management plane.
///
/// Live mode: the workload identity needs <b>Azure Service Bus Data Owner</b> on the namespace so
/// Wolverine can create topics/queues/subscriptions on first connect. Bind the existing namespace via
/// <c>AsExisting(...)</c> on the returned builder when wiring publish.
/// </summary>
internal static class MessagingExtensions
{
    /// <summary>The Aspire endpoint name of the emulator's HTTP management/health port (container
    /// port 5300). Aspire marks this endpoint with <c>ExcludeReferenceEndpoint=true</c>, so it doesn't
    /// flow through <c>WithReference(messaging)</c> — we surface it explicitly as a second connection
    /// string in <see cref="WithMusterMessaging"/>.</summary>
    private const string EmulatorHealthEndpointName = "emulatorhealth";

    /// <summary>Adds the <c>messaging</c> Azure Service Bus resource. In run mode the namespace is
    /// replaced by the Microsoft Service Bus emulator container with fixed ports for predictable
    /// cross-tool wiring (AMQP on 5672, HTTP admin on 5300).</summary>
    public static IResourceBuilder<AzureServiceBusResource> AddMusterServiceBus(this IDistributedApplicationBuilder builder)
    {
        var messaging = builder.AddAzureServiceBus("messaging");

        if (builder.ExecutionContext.IsRunMode)
        {
            messaging.RunAsEmulator(container =>
            {
                // Aspire generates a default Config.json for the emulator; we replace UserConfig with the
                // exact shape we want: a single namespace (the emulator-conventional "sbemulatorns") and
                // file logging (writes to /home/app/EmulatorLogs inside the container, viewable via the
                // dashboard's Files tab). Entities (topics/queues/subs) are intentionally NOT listed
                // here — Wolverine creates them via AutoProvision on first connect.
                container.WithConfiguration(config =>
                {
                    config["UserConfig"] = new JsonObject
                    {
                        ["Namespaces"] = new JsonArray
                        {
                            new JsonObject { ["Name"] = "sbemulatorns" },
                        },
                        ["Logging"] = new JsonObject { ["Type"] = "File" },
                    };
                });

                //container
                //    .WithHostPort(5672)
                //    .WithEndpoint(port: 5300, targetPort: 5300, isProxied: false);
            });
        }

        return messaging;
    }

    /// <summary>Wires a consumer project to the messaging namespace: <c>WithReference</c> +
    /// <c>WaitFor</c>, plus (in run mode) the emulator's management connection string so Wolverine can
    /// run <c>AutoProvision</c> against the management endpoint. In publish the management plane is
    /// reached via the same FQDN + managed identity, so no extra wiring is needed.</summary>
    public static IResourceBuilder<TProject> WithMusterMessaging<TProject>(
        this IResourceBuilder<TProject> consumer,
        IResourceBuilder<AzureServiceBusResource> messaging)
        where TProject : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        consumer.WithReference(messaging).WaitFor(messaging);

        if (consumer.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            var health = messaging.GetEndpoint(EmulatorHealthEndpointName);
            var managementConnectionString = ReferenceExpression.Create(
                $"Endpoint=sb://{health.Property(EndpointProperty.Host)}:{health.Property(EndpointProperty.Port)};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;");

            consumer.WithEnvironment("ConnectionStrings__messaging-management", managementConnectionString);
        }

        return consumer;
    }
}
