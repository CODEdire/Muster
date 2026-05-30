using Aspire.Hosting.Azure;
using Azure.Core;
using Azure.Provisioning.AppContainers;

namespace Muster.AppHost;

/// <summary>
/// AppHost composition for the web (Blazor + API). Mirrors the per-feature extension pattern.
///
/// <para><b>Run mode</b>: standard Aspire-managed dotnet process — fast inner loop with hot reload
/// and debugger attach.</para>
///
/// <para><b>Publish mode</b>: emitted as a Container App via <c>PublishAsAzureContainerApp</c>.
/// Sized for typical Blazor SSR + Wolverine workload; scales 1..5 on HTTP RPS / CPU. Termination
/// grace 30s — enough for in-flight HTTP requests + Blazor circuit disconnects to drain cleanly.</para>
///
/// <para>External HTTPS endpoint exposed (the public face of Muster). Custom domain + managed cert
/// binding is a post-deploy step — see <c>docs/deployment.md</c> "Custom domain + SSL".</para>
/// </summary>
internal static class WebHostingExtensions
{
    public const string ResourceName = "web";

    public static IResourceBuilder<ProjectResource> AddMusterWeb(
        this IDistributedApplicationBuilder builder,
        IResourceBuilder<AzureSqlDatabaseResource> db,
        IResourceBuilder<AzureServiceBusResource> messaging,
        IResourceBuilder<ParameterResource> discordToken,
        IResourceBuilder<ParameterResource> discordClientId,
        IResourceBuilder<ParameterResource> discordClientSecret,
        IResourceBuilder<ProjectResource> migrations,
        IResourceBuilder<AzureKeyVaultResource>? keyVault,
        IResourceBuilder<AzureAppConfigurationResource>? appConfig,
        IResourceBuilder<AzureBlobStorageContainerResource> dpKeys,
        IResourceBuilder<AzureApplicationInsightsResource>? appInsights)
    {
        var web = builder.AddProject<Projects.Muster_Web>(ResourceName)
            .WithReference(db)
            .WithMusterMessaging(messaging)
            .WithReference(dpKeys)
            .WithEnvironment("Discord__ClientId", discordClientId)
            .WithEnvironment("Discord__ClientSecret", discordClientSecret)
            // Bot token lets the web settings page list a guild's channels (quest-board channel picker)
            // via Discord REST — no channel table, fetched live.
            .WithEnvironment("Discord__Token", discordToken)
            // Data Protection wiring — both URIs are read by Infrastructure.AddMusterConnectorProtection
            // to decide between Azure DP (Blob + KV wrap) and the local SQL fallback.
            .WithEnvironment("DataProtection__WrapKeyName", KeyVaultConstants.DataProtectionWrapKeyName)
            .WithEnvironment("DataProtection__Container", StorageConstants.DataProtectionContainerName)
            .WithExternalHttpEndpoints()
            .WaitForCompletion(migrations);

        // KV + AppConfig are publish-only (null in run mode). WithReference grants the workload identity
        // the right RBAC role + publishes ConnectionStrings:kv / ConnectionStrings:appconfig.
        if (keyVault is not null)
        {
            web.WithReference(keyVault);
        }
        if (appConfig is not null)
        {
            web.WithReference(appConfig);
        }
        // App Insights ref publishes APPLICATIONINSIGHTS_CONNECTION_STRING which UseAzureMonitor() in
        // ServiceDefaults picks up. Null in run mode → telemetry stays on the Aspire Dashboard via OTLP.
        if (appInsights is not null)
        {
            web.WithReference(appInsights);
        }

        // Optional custom domain — read from azd env vars (set via `azd env set webCustomDomain ...`).
        // Both must be set together: the hostname AND the resource id of an already-issued managed cert
        // on the CA Environment. Bootstrap pattern: first deploy omits the binding, you add the hostname
        // + cert via Portal, then copy the cert resource id into azd env vars so subsequent `azd up`
        // runs preserve the binding (without this, every redeploy strips the manually-added domain).
        //
        // Read via Environment.GetEnvironmentVariable directly — azd injects its .env entries as process
        // env vars when launching the AppHost, but builder.Configuration's resolution can miss them
        // depending on prefix/casing rules. The env-var API is the path azd guarantees.
        var customDomain = Environment.GetEnvironmentVariable("webCustomDomain");
        var customDomainCertId = Environment.GetEnvironmentVariable("webCustomDomainCertId");
        var hasCustomDomain = !string.IsNullOrWhiteSpace(customDomain)
                              && !string.IsNullOrWhiteSpace(customDomainCertId);

        // Diagnostic — visible in `azd up` console + Aspire dashboard so you can confirm the binding
        // is being emitted (or NOT, and why). Strip once the wiring is proven stable.
        Console.WriteLine($"[muster-web] custom domain: " +
            (hasCustomDomain ? $"BINDING '{customDomain}' to cert '{customDomainCertId![..Math.Min(80, customDomainCertId.Length)]}…'"
                             : $"SKIPPED (webCustomDomain='{customDomain}', webCustomDomainCertId set: {!string.IsNullOrEmpty(customDomainCertId)})"));

        if (builder.ExecutionContext.IsPublishMode)
        {
            web.PublishAsAzureContainerApp((_, app) =>
            {
                var container = app.Template.Containers[0].Value!;
                container.Resources.Cpu = 0.5;
                container.Resources.Memory = "1.0Gi";

                // Keep one warm replica so Blazor InteractiveServer circuits don't drop to zero;
                // scale up on traffic. Raise MaxReplicas as load grows.
                app.Template.Scale.MinReplicas = 1;
                app.Template.Scale.MaxReplicas = 1;

                // 30s drain — HTTP requests + Blazor circuit disconnects + Wolverine outbox flush.
                app.Template.TerminationGracePeriodSeconds = 30;

                // Single-revision mode: new revision replaces old atomically once readiness passes.
                // Switch to Multiple if you ever want canary % splits on web.
                app.Configuration.ActiveRevisionsMode = ContainerAppActiveRevisionsMode.Single;

                // ACA probes against the container's ingress target port — paths come from
                // ServiceDefaults.MapDefaultEndpoints. Startup covers ASP.NET warmup, Liveness restarts
                // on a deadlocked process, Readiness gates traffic on dependency health (DB + Wolverine).
                //
                // Port is hardcoded to 8080: .NET 8+ SDK container images expose 8080 by convention and
                // Aspire wires Ingress.TargetPort to match. Reading Configuration.Ingress.TargetPort here
                // returns 0 because the value isn't materialised until later in Aspire's pipeline, which
                // tripped a ContainerAppProbeInvalidPort preflight error during `azd up`.
                const int targetPort = 8080;

                // Startup: gives ASP.NET ~150s to come up before Liveness starts evaluating. Generous —
                // a cold container + EF first-query warmup + Wolverine handler graph compile can take
                // 30-60s on lower CPU tiers. Failing here just delays revision activation, not restart.
                container.Probes.Add(new ContainerAppProbe
                {
                    ProbeType = ContainerAppProbeType.Startup,
                    HttpGet = new ContainerAppHttpRequestInfo
                    {
                        Path = "/alive",
                        Port = targetPort,
                        Scheme = ContainerAppHttpScheme.Http,
                    },
                    InitialDelaySeconds = 5,
                    PeriodSeconds = 5,
                    TimeoutSeconds = 3,
                    FailureThreshold = 30, // 30 * 5s = 150s startup grace
                });

                // Liveness: only the "self" check (no I/O — see ServiceDefaults). 3 fails in a row = restart.
                container.Probes.Add(new ContainerAppProbe
                {
                    ProbeType = ContainerAppProbeType.Liveness,
                    HttpGet = new ContainerAppHttpRequestInfo
                    {
                        Path = "/alive",
                        Port = targetPort,
                        Scheme = ContainerAppHttpScheme.Http,
                    },
                    PeriodSeconds = 30,
                    TimeoutSeconds = 5,
                    FailureThreshold = 3,
                });

                // Readiness: every registered check (DbContext CanConnect + Wolverine.AssertHasStarted).
                // Failure removes the replica from traffic until it recovers — no restart. Lets a DB
                // blip drain in-flight requests without nuking the container.
                container.Probes.Add(new ContainerAppProbe
                {
                    ProbeType = ContainerAppProbeType.Readiness,
                    HttpGet = new ContainerAppHttpRequestInfo
                    {
                        Path = "/health",
                        Port = targetPort,
                        Scheme = ContainerAppHttpScheme.Http,
                    },
                    PeriodSeconds = 10,
                    TimeoutSeconds = 5,
                    FailureThreshold = 3,
                    SuccessThreshold = 1,
                });

                // Custom domain binding — only when both azd env vars are set. Keeps the hostname +
                // managed cert reference declared in the Aspire model so subsequent `azd up` runs
                // preserve them instead of stripping the manually-added Portal binding.
                if (hasCustomDomain)
                {
                    app.Configuration.Ingress.CustomDomains.Add(new ContainerAppCustomDomain
                    {
                        Name = customDomain!,
                        CertificateId = new ResourceIdentifier(customDomainCertId!),
                        BindingType = ContainerAppCustomDomainBindingType.SniEnabled,
                    });
                }
            });
        }

        return web;
    }
}
