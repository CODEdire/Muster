using Azure.Provisioning;
using Azure.Provisioning.AppConfiguration;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.ApplicationInsights;
using Azure.Provisioning.ContainerRegistry;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.KeyVault;
using Azure.Provisioning.OperationalInsights;
using Azure.Provisioning.Primitives;
using Azure.Provisioning.ServiceBus;
using Azure.Provisioning.SignalR;
using Azure.Provisioning.Sql;
using Azure.Provisioning.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System;

namespace Muster.AppHost.Core;

/// <summary>
/// Forces a consistent, human-readable name on every <i>newly provisioned</i> Azure resource:
/// <c>{prefix}-muster-{env}-{loc}-{token}</c> (e.g. <c>appcs-muster-prod-scus-a1b2c</c>).
///
/// <para>The trailing <c>{token}</c> is Aspire's default deterministic uniqueness suffix —
/// <c>uniqueString(resourceGroup().id)</c>, resolved at deploy time. Keeping it means names stay unique
/// per deployment target (resource group) and stable across redeploys, while the prefix is readable.</para>
///
/// <para><b>New deployments only.</b> Resources bound via <c>AsExisting</c>/<c>PublishAsExisting</c> report
/// <see cref="ProvisionableResource.IsExistingResource"/> = true and are left untouched, so existing
/// resources keep their current names.</para>
/// </summary>
internal sealed class FixedNameInfrastructureResolver(IHostEnvironment environment, IConfiguration configuration)
    : InfrastructureResolver
{
    public override void ResolveProperties(ProvisionableConstruct construct, ProvisioningBuildOptions options)
    {
        string env = environment.EnvironmentName switch
        {
            "Development" => "dev",
            "Production" => "prod",
            "Staging" => "staging",
            _ => throw new InvalidOperationException($"Unexpected environment name: {environment.EnvironmentName}")
        };

        // Region for the name slug — not derivable from resourceGroup().location at deploy time, so it's read from
        // config. "Azure:Location" is Aspire's own provisioning key (azd's equivalent is the AZURE_LOCATION env
        // var; we accept either). IMPORTANT: `aspire deploy`/`publish` runs the AppHost as Production, and
        // user-secrets load only in Development — so a value put ONLY in user-secrets is invisible at deploy time.
        // Put "Azure:Location" in appsettings(.{Environment}).json, or export AZURE_LOCATION in the shell first.
        string loc = (configuration["Azure:Location"]
            ?? Environment.GetEnvironmentVariable("AZURE_LOCATION")
            ?? throw new InvalidOperationException(
                "Missing region. Set 'Azure:Location' in appsettings (or export AZURE_LOCATION before `aspire deploy`), e.g. 'southcentralus'."))
            .ToAbbreviation();

        switch (construct)
        {
            case AppConfigurationStore { IsExistingResource: false } appConfig:
                appConfig.Name = Compose("appcs", env, loc);
                break;

            case ContainerRegistryService { IsExistingResource: false } containerRegistryService:
                containerRegistryService.Name = Compose("acr", env, loc);
                break;

            case ContainerAppManagedEnvironment { IsExistingResource: false } appContainerEnv:
                appContainerEnv.Name = Compose("cae", env, loc);
                break;

            case ServiceBusNamespace { IsExistingResource: false } sb:
                sb.Name = Compose("sb", env, loc);
                break;

            case KeyVaultService { IsExistingResource: false } kv:
                // Key Vault max 24 chars — and it DOES allow hyphens (unlike ACR/storage), so keep them.
                // "kv-muster-{env}-{loc}-" = 20 chars, leaving a 4-char uniqueness token (= the Compose floor).
                kv.Name = Compose("kv", env, loc, maxLength: 24);
                break;

            case StorageAccount { IsExistingResource: false } storage:
                // Storage: no hyphens, lowercase, max 24.
                storage.Name = Compose("st", env, loc, maxLength: 24, hyphens: false);
                break;

            case ApplicationInsightsComponent { IsExistingResource: false } appi:
                appi.Name = Compose("appi", env, loc);
                break;

            case OperationalInsightsWorkspace { IsExistingResource: false } law:
                law.Name = Compose("log", env, loc);
                break;

            case SignalRService { IsExistingResource: false } signalr:
                signalr.Name = Compose("sigr", env, loc);
                break;

            case SqlServer { IsExistingResource: false } sqlServer:
                // SQL server names: lowercase, max 63. (The database name is set explicitly, not here.)
                sqlServer.Name = Compose("sql", env, loc, hyphens: false);
                break;
        }

        base.ResolveProperties(construct, options);
    }

    /// <summary>
    /// Builds a Bicep interpolated name <c>{prefix}[-]muster[-]{env}[-]{loc}[-]{token}</c> where the token is
    /// the deploy-time <c>uniqueString(resourceGroup().id)</c>, truncated so the whole name fits
    /// <paramref name="maxLength"/>.
    /// </summary>
    private static BicepValue<string> Compose(
        string prefix, string env, string loc, int maxLength = 50, bool hyphens = true)
    {
        string sep = hyphens ? "-" : "";
        string fixedPart = $"{prefix}{sep}muster{sep}{env}{sep}{loc}{sep}";

        // uniqueString() always returns 13 chars; cap so prefix + token ≤ maxLength. Floor 4 (not 5) so the
        // tightest hyphenated name — Key Vault's "kv-muster-{env}-{loc}-" at 20 chars — still fits 24.
        int tokenLen = Math.Clamp(maxLength - fixedPart.Length, 4, 13);
        var token = BicepFunction.Take(
            BicepFunction.GetUniqueString(BicepFunction.GetResourceGroup().Id), tokenLen);

        var name = BicepFunction.Interpolate($"{fixedPart}{token}");
        return hyphens ? name : BicepFunction.ToLower(name);
    }
}
