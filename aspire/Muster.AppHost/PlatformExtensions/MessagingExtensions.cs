using Aspire.Hosting.Azure;
using Azure.Provisioning.ServiceBus;
using Microsoft.Extensions.Configuration;
using Muster.AppHost.Core;
using Muster.AppHost.Options;
using System.Text.Json.Nodes;

namespace Muster.AppHost.PlatformExtensions;

internal static class MessagingConstants
{
    public const string ReferenceName = "messaging";

    public const string ResourceNameParam = "serviceBusName";
    public const string ResourceGroupNameParam = "serviceBusResourceGroupName";

    public const string EmulatorConfig =
    @"{
        ""UserConfig"": {
            ""Namespaces"": [
                {
                    ""Name"": ""sbemulatorns""
                }
            ],
            ""Logging"": {
                ""Type"": ""File""
            }
        }        
    }";
}

internal static class MessagingExtensions
{
    private const string EmulatorHealthEndpointName = "emulatorhealth";


    public static MusterPlatformBuilder AddMessaging(this MusterPlatformBuilder p)
    {
        var builder = p.Inner;
        var config = builder.Configuration.GetSection(nameof(MessagingOptions)).Get<MessagingOptions>();
        var resource = builder.AddAzureServiceBus(MessagingConstants.ReferenceName);

        resource.RunAsEmulator(container => container.WithConfiguration(config => JsonNode.Parse(MessagingConstants.EmulatorConfig)));

        if (builder.ExecutionContext.IsPublishMode && config?.UseExisting == true)
        {
            resource.PublishAsExisting(
                builder.AddParameter(MessagingConstants.ResourceNameParam),
                builder.AddParameter(MessagingConstants.ResourceGroupNameParam)
            );
        }

        resource.ConfigureInfrastructure(infrastructure =>
        {
            var sb = infrastructure.GetProvisionableResources()
                .OfType<ServiceBusNamespace>()
                .Single();

            if (!sb.IsExistingResource && config is not null)
            {
                sb.IsZoneRedundant = config.IsZoneRedundant;
                sb.DisableLocalAuth = config.DisableLocalAuth;
                sb.PublicNetworkAccess = config.PublicNetworkAccess;

                //Consider TLS 1.3?

                sb.Sku = new ServiceBusSku()
                {
                    Name = ServiceBusSkuName.Standard //Standard needed for Queues/Topics
                };
            }
        });

        //Enable Emulator container for dev/test runs
        if (builder.ExecutionContext.IsRunMode)
            builder.AddMusterServiceBusEmulatorUi(resource);

        p.Messaging = resource;
        return p;
    }


    public static IResourceBuilder<TProject> WithMusterMessaging<TProject>(
        this IResourceBuilder<TProject> consumer,
        IResourceBuilder<AzureServiceBusResource> messaging)
        where TProject : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        consumer
            .WithReference(messaging)
            .WithRoleAssignments(messaging, ServiceBusBuiltInRole.AzureServiceBusDataOwner)
            .WaitFor(messaging);

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
