using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning.AppContainers;
using Microsoft.Extensions.Configuration;
using Muster.AppHost.Core;
using Muster.AppHost.Options;
using Muster.AppHost.PlatformExtensions;

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
        this IDistributedApplicationBuilder builder, MusterPlatform platform)
    {
        // All shared infra (SQL, Service Bus, Data Protection, Key Vault, App Config, App Insights, migration gate)
        // + their RBAC role assignments are wired by WithMusterPlatform — see PlatformWiringExtensions.
        var web = builder.AddProject<Projects.Muster_Web>(ResourceName)
            .WithMusterPlatform(platform)
            .WithEnvironment("Discord__ClientId", platform.DiscordClientId)
            .WithEnvironment("Discord__ClientSecret", platform.DiscordClientSecret)
            // Bot token lets the web settings page list a guild's channels (quest-board channel picker)
            // via Discord REST — no channel table, fetched live.
            .WithEnvironment("Discord__Token", platform.DiscordToken)
            .WithExternalHttpEndpoints();

        // Azure SignalR backplane (web-only; bot has no circuit). Publish only — null in run mode keeps the
        // local circuit in-process. Grants the SignalR App Server role + publishes ConnectionStrings:signalr.
        if (platform.SignalR is not null)
        {
            web.WithMusterSignalR(platform.SignalR);
        }

        // Optional custom domain + managed TLS. Skipped entirely when no Domain is configured; the app keeps its
        // default *.azurecontainerapps.io host. Wired via Aspire's ConfigureCustomDomain helper (below), which
        // takes two parameter resources: the hostname and the managed-cert NAME.
        //
        // The HOSTNAME is Options-driven (WebCustomDomainOptions:Domain) — known up front. The CERT NAME is a
        // PROMPTED parameter, because it isn't known until the managed cert is issued, which can't happen until
        // the first deploy puts the hostname live so DNS can validate. Two-phase bootstrap (see deployment.md):
        //   1. First deploy: leave the cert-name prompt BLANK → hostname binds unbound (bindingType Disabled).
        //   2. Issue the managed cert, then deploy again and enter its name → TLS attaches (SniEnabled).
        // CI / non-interactive deploys supply the value via config key `Parameters:webCustomDomainCertificateName`
        // (AddParameter reads that first and only prompts when it's absent).
        var domainCfg = builder.Configuration.GetSection(nameof(WebCustomDomainOptions)).Get<WebCustomDomainOptions>();
        IResourceBuilder<ParameterResource>? customDomainParam = null;
        IResourceBuilder<ParameterResource>? certificateNameParam = null;
        if (builder.ExecutionContext.IsPublishMode && !string.IsNullOrWhiteSpace(domainCfg?.Domain))
        {
            customDomainParam = builder.AddParameter("webCustomDomain", domainCfg.Domain);
            certificateNameParam = builder.AddParameter("webCustomDomainCertificateName");
        }

        // Container sizing + autoscale from config (appsettings / user-secrets / App Configuration).
        var cfg = builder.Configuration.GetSection(nameof(WebContainerOptions)).Get<WebContainerOptions>() ?? new();

        if (builder.ExecutionContext.IsPublishMode)
        {
            web.PublishAsAzureContainerApp((_, app) =>
            {
                var container = app.Template.Containers[0].Value!;
                container.Resources.Cpu = cfg.Cpu;
                container.Resources.Memory = cfg.Memory;

                // MinReplicas keeps warm replicas so Blazor circuits don't drop to zero; MaxReplicas caps scale-out.
                app.Template.Scale.MinReplicas = cfg.MinReplicas;
                app.Template.Scale.MaxReplicas = cfg.MaxReplicas;

                // Drain window — HTTP requests + Blazor circuit disconnects + Wolverine outbox flush.
                app.Template.TerminationGracePeriodSeconds = cfg.TerminationGracePeriodSeconds;

                // Single-revision mode: new revision replaces old atomically once readiness passes.
                // Switch to Multiple if you ever want canary % splits on web.
                app.Configuration.ActiveRevisionsMode = ContainerAppActiveRevisionsMode.Single;

                // Scale-out (MaxReplicas > 1) is only correct for Blazor Server with BOTH the Azure SignalR backplane
                // (wired above) AND ingress session affinity — the circuit's component state still lives on one
                // replica, so a client must keep landing on the replica that owns its circuit. The HTTP rule is what
                // actually triggers scaling (without a rule ACA stays at MinReplicas).
                if (cfg.MaxReplicas > 1)
                {
                    app.Configuration.Ingress.StickySessionsAffinity = StickySessionAffinity.Sticky;

                    if (cfg.HttpScaleConcurrentRequests > 0)
                    {
                        var httpRule = new ContainerAppScaleRule { Name = "http-concurrency", Http = new ContainerAppHttpScaleRule() };
                        httpRule.Http.Metadata.Add("concurrentRequests", cfg.HttpScaleConcurrentRequests.ToString());
                        app.Template.Scale.Rules.Add(httpRule);
                    }
                }

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

                // Custom domain — declared in the Aspire model so every deploy preserves the binding instead of
                // stripping a manually-added one. ConfigureCustomDomain emits the ingress customDomain + (once
                // CertificateName is set) the managed-cert binding. Empty cert name = hostname bound unbound,
                // which is the intended first-deploy state while DNS validation completes.
                if (customDomainParam is not null && certificateNameParam is not null)
                {
#pragma warning disable ASPIREACADOMAINS001 // ConfigureCustomDomain is [Experimental] in Aspire 13.3.5;
                    // when it stabilises the suppression drops out, the call stays identical.
                    app.ConfigureCustomDomain(customDomainParam, certificateNameParam);
#pragma warning restore ASPIREACADOMAINS001
                }
            });
        }

        return web;
    }
}
