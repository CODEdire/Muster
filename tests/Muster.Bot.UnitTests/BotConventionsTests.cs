using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Muster.Bot.Platform;
using NetCord.Hosting.Gateway;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;
using Xunit;

namespace Muster.Bot.UnitTests;

/// <summary>
/// Reflection guards that catch organizational drift in the bot's feature/function layout. Each rule pins a
/// convention the runtime depends on but the compiler doesn't enforce — e.g. a "handler" file that doesn't
/// actually implement a gateway-handler interface would be silently dead code after the explicit-registration
/// refactor (no assembly scan to silently include it).
/// </summary>
public class BotConventionsTests
{
    private static Assembly BotAssembly => typeof(MusterModuleBase).Assembly;

    /// <summary>Runs every Add&lt;Feature&gt;Feature on a fresh host builder and returns the resulting
    /// service collection. Pure DI registration — no Build(), no Discord, no DB — so this is safe in unit tests.</summary>
    private static IServiceCollection RegisteredServices()
    {
        var builder = Host.CreateApplicationBuilder();
        builder
            .AddQuestingFeature()
            .AddEconomyFeature()
            .AddTrackingFeature()
            .AddMustersFeature()
            .AddMembershipFeature()
            .AddConfigFeature()
            .AddSeasonsFeature()
            .AddOpsFeature()
            .AddPlatformFeature();
        return builder.Services;
    }

    private static bool IsRegistered(IServiceCollection services, Type type) =>
        services.Any(d => d.ServiceType == type || d.ImplementationType == type);

    private static readonly string[] Features =
    [
        "Config", "Economy", "Membership", "Musters", "Ops", "Platform", "Questing", "Seasons", "Tracking",
    ];

    /// <summary>Every concrete public type whose namespace ends with <c>.Handlers</c> must implement at least
    /// one NetCord gateway-handler interface OR be a Wolverine message handler (a static class whose
    /// <c>Handle</c> method takes a Muster.Contracts type). Catches a "handler" file the developer forgot to
    /// wire up.</summary>
    [Fact]
    public void HandlersFolder_ContainsOnlyRealHandlers()
    {
        var orphans = new List<string>();
        foreach (var t in BotAssembly.GetTypes())
        {
            if (t.Namespace is null || !t.Namespace.EndsWith(".Handlers", StringComparison.Ordinal))
            {
                continue;
            }
            if (!t.IsPublic && !t.IsNestedPublic)
            {
                continue;
            }
            if (t.IsAbstract && !t.IsSealed) // pure abstract — skip
            {
                continue;
            }

            // Marker classes (empty sealed types used only as ILogger<T> category symbols) are not handlers;
            // they're allowed to coexist in the Handlers folder for log-readability. Skip.
            var declaredMembers = t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m is not ConstructorInfo)
                .ToList();
            if (declaredMembers.Count == 0)
            {
                continue;
            }

            // NetCord gateway handler: implements at least one I*GatewayHandler interface.
            var hasGatewayInterface = t.GetInterfaces().Any(i =>
                i.Namespace == "NetCord.Hosting.Gateway" &&
                i.Name.EndsWith("GatewayHandler", StringComparison.Ordinal));

            // Wolverine message handler: has a method named Handle whose first param's namespace starts with Muster.Contracts.
            var hasWolverineHandle = t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == "Handle")
                .Any(m =>
                {
                    var first = m.GetParameters().FirstOrDefault();
                    return first?.ParameterType.Namespace?.StartsWith("Muster.Contracts", StringComparison.Ordinal) == true;
                });

            if (!hasGatewayInterface && !hasWolverineHandle)
            {
                orphans.Add(t.FullName!);
            }
        }

        Assert.True(orphans.Count == 0,
            "Files in a .Handlers namespace must implement a NetCord gateway interface or be a Wolverine handler.\nOrphans:\n  " +
            string.Join("\n  ", orphans));
    }

    /// <summary>Every concrete public type whose namespace ends with <c>.Modules</c> and is decorated with
    /// <see cref="SlashCommandAttribute"/> must inherit (directly or indirectly) from <see cref="MusterModuleBase"/>.
    /// This is what gives every slash command the shared run/auth/audit pipeline; bypassing it silently skips
    /// the audit row + role check.</summary>
    [Fact]
    public void SlashModules_InheritMusterModuleBase()
    {
        var violations = new List<string>();
        foreach (var t in BotAssembly.GetTypes())
        {
            if (t.Namespace is null || !t.Namespace.EndsWith(".Modules", StringComparison.Ordinal))
            {
                continue;
            }
            if (!t.IsPublic && !t.IsNestedPublic)
            {
                continue;
            }
            if (t.GetCustomAttribute<SlashCommandAttribute>() is null)
            {
                continue;
            }

            if (!typeof(MusterModuleBase).IsAssignableFrom(t))
            {
                violations.Add(t.FullName!);
            }
        }

        Assert.True(violations.Count == 0,
            "Slash command modules must inherit MusterModuleBase for the shared auth/audit pipeline.\nViolations:\n  " +
            string.Join("\n  ", violations));
    }

    /// <summary>Component interaction modules must inherit <see cref="ComponentInteractionModule{TContext}"/>.
    /// NetCord won't discover them otherwise (and the build does for routing tests, but a malformed inherit
    /// would slip through if the class was discovered some other way).</summary>
    [Fact]
    public void ComponentInteractionModules_InheritNetCordBase()
    {
        var violations = new List<string>();
        var openBase = typeof(ComponentInteractionModule<>);

        foreach (var t in BotAssembly.GetTypes())
        {
            if (t.Namespace is null || !t.Namespace.EndsWith(".Modules", StringComparison.Ordinal))
            {
                continue;
            }
            if (!t.IsPublic && !t.IsNestedPublic)
            {
                continue;
            }

            // ComponentInteraction modules are named *InteractionModule; verify they actually inherit the base.
            if (!t.Name.EndsWith("InteractionModule", StringComparison.Ordinal))
            {
                continue;
            }

            var inheritsBase = false;
            for (var b = t.BaseType; b is not null; b = b.BaseType)
            {
                if (b.IsGenericType && b.GetGenericTypeDefinition() == openBase)
                {
                    inheritsBase = true;
                    break;
                }
            }

            if (!inheritsBase)
            {
                violations.Add(t.FullName!);
            }
        }

        Assert.True(violations.Count == 0,
            "Types named *InteractionModule in a .Modules namespace must inherit ComponentInteractionModule<TContext>.\nViolations:\n  " +
            string.Join("\n  ", violations));
    }

    /// <summary>Each feature folder must expose an <c>Add&lt;Feature&gt;Feature(IHostApplicationBuilder)</c>
    /// extension. Program.cs's chain depends on this — a missing one silently drops the feature.</summary>
    [Fact]
    public void EveryFeature_HasAddFeatureExtension()
    {
        var missing = new List<string>();
        foreach (var feature in Features)
        {
            var extensionsType = BotAssembly.GetType($"Muster.Bot.{feature}.{feature}Extensions");
            if (extensionsType is null)
            {
                missing.Add($"Muster.Bot.{feature}.{feature}Extensions (type missing)");
                continue;
            }

            var methodName = $"Add{feature}Feature";
            var method = extensionsType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            if (method is null)
            {
                missing.Add($"{extensionsType.FullName}.{methodName}");
            }
        }

        Assert.True(missing.Count == 0,
            "Every feature must publish an Add<Feature>Feature extension method.\nMissing:\n  " +
            string.Join("\n  ", missing));
    }

    /// <summary>Each feature that has slash command modules must expose a
    /// <c>Use&lt;Feature&gt;Module(IHost)</c> extension. Mirrors ASP.NET's <c>app.UseXxx</c> chain.</summary>
    [Fact]
    public void EveryFeatureWithModules_HasUseModuleExtension()
    {
        var missing = new List<string>();
        foreach (var feature in Features)
        {
            // Find slash modules in this feature's namespace.
            var hasModules = BotAssembly.GetTypes().Any(t =>
                t.Namespace is { } ns &&
                ns.StartsWith($"Muster.Bot.{feature}", StringComparison.Ordinal) &&
                t.GetCustomAttribute<SlashCommandAttribute>() is not null);

            if (!hasModules)
            {
                continue;
            }

            var extensionsType = BotAssembly.GetType($"Muster.Bot.{feature}.{feature}Extensions");
            var methodName = $"Use{feature}Module";
            var method = extensionsType?.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            if (method is null)
            {
                missing.Add($"{feature} has slash modules but no {extensionsType?.FullName ?? "<Extensions>"}.{methodName}");
            }
        }

        Assert.True(missing.Count == 0,
            "Features that own slash modules must publish Use<Feature>Module.\nMissing:\n  " +
            string.Join("\n  ", missing));
    }

    /// <summary>Every NetCord gateway handler in the bot must be wired by some feature's
    /// <c>Add&lt;Feature&gt;Feature</c>. The assembly-scan registration is gone (explicit only), so a handler
    /// without an explicit <c>services.AddGatewayHandler&lt;T&gt;()</c> is silently dead — Discord events for
    /// that type never dispatch. This test catches "I added a handler but forgot to register it."</summary>
    [Fact]
    public void EveryGatewayHandler_IsRegisteredByAFeature()
    {
        var services = RegisteredServices();
        var handlers = BotAssembly.GetTypes()
            .Where(t => (t.IsPublic || t.IsNestedPublic) && !t.IsAbstract)
            .Where(t => t.GetInterfaces().Any(i =>
                i.Namespace == "NetCord.Hosting.Gateway" &&
                i.Name.EndsWith("GatewayHandler", StringComparison.Ordinal)))
            .ToList();

        var unregistered = handlers.Where(h => !IsRegistered(services, h)).Select(t => t.FullName!).ToList();

        Assert.True(unregistered.Count == 0,
            "Gateway handlers must be registered via services.AddGatewayHandler<T>() in their feature's extensions.\nUnregistered:\n  " +
            string.Join("\n  ", unregistered));
    }

    /// <summary>Every <c>IHostedService</c> implementation in the bot must be registered by some feature's
    /// <c>Add&lt;Feature&gt;Feature</c>. Same drift problem as gateway handlers: a scheduler that's
    /// instantiable but unregistered will never tick.</summary>
    [Fact]
    public void EveryHostedService_IsRegisteredByAFeature()
    {
        var services = RegisteredServices();
        var schedulers = BotAssembly.GetTypes()
            .Where(t => (t.IsPublic || t.IsNestedPublic) && !t.IsAbstract)
            .Where(t => typeof(IHostedService).IsAssignableFrom(t))
            .ToList();

        // IHostedService registers under the IHostedService service type, so check implementation type.
        var unregistered = schedulers
            .Where(s => !services.Any(d => d.ImplementationType == s))
            .Select(t => t.FullName!)
            .ToList();

        Assert.True(unregistered.Count == 0,
            "IHostedService implementations must be registered via services.AddHostedService<T>() in their feature's extensions.\nUnregistered:\n  " +
            string.Join("\n  ", unregistered));
    }

    /// <summary>The Bot is organized feature-first; no business-feature .cs file should leak back to the
    /// root <c>Muster.Bot</c> namespace (only <c>Program</c> + global usings live there). Catches
    /// a moved file whose namespace declaration didn't get updated.</summary>
    [Fact]
    public void NoBusinessTypes_InRootNamespace()
    {
        var leaks = BotAssembly.GetTypes()
            .Where(t => t.Namespace == "Muster.Bot")
            .Where(t => t.IsPublic || t.IsNestedPublic)
            .Where(t => t.Name != "Program" && !t.Name.StartsWith('<')) // top-level statements generate <Program>$
            .Select(t => t.FullName!)
            .ToList();

        Assert.True(leaks.Count == 0,
            "All bot types live under Muster.Bot.<Feature>. Files at the root namespace are leaks.\nLeaks:\n  " +
            string.Join("\n  ", leaks));
    }
}
