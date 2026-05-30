using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Aspire service defaults: OpenTelemetry, health checks, service discovery and HTTP resilience.
/// Shared by every Muster service that hosts work (bot, web, migrations).
/// </summary>
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    // Wolverine's built-in Meters — handler durations, message counts. Wolverine names its
                    // meters Wolverine:{ApplicationName}, so the wildcard match catches them all.
                    .AddMeter("Wolverine*")
                    // App-specific Meter (Muster.Bot / Muster.Web) — custom counters/histograms.
                    .AddMeter(builder.Environment.ApplicationName);
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    // Wolverine emits spans for every message send + handler invocation under "Wolverine";
                    // registering once here means the bot's QuestLifecycleNotified handler shows up in App
                    // Insights' end-to-end transaction view chained from the web's publishing span.
                    .AddSource("Wolverine");
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        // OTLP → Aspire Dashboard locally. Aspire injects OTEL_EXPORTER_OTLP_ENDPOINT into every project
        // resource in run mode; absent in publish.
        var useOtlpExporter = !string.IsNullOrWhiteSpace(
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Azure Monitor → App Insights in publish. WithReference(appInsights) on each project in the
        // AppHost publishes APPLICATIONINSIGHTS_CONNECTION_STRING; presence of that var is the gate. The
        // exporter rides the existing OTEL pipeline configured above — no separate instrumentation list.
        var appInsightsConn = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]
                              ?? builder.Configuration.GetConnectionString("appinsights");
        if (!string.IsNullOrWhiteSpace(appInsightsConn))
        {
            builder.Services.AddOpenTelemetry().UseAzureMonitor(o => o.ConnectionString = appInsightsConn);
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// Maps probe endpoints in <b>every</b> environment. ACA needs them reachable in prod so the orchestrator
    /// can issue liveness + readiness probes; without them ACA falls back to TCP-port checks, which can't tell
    /// "process up but deadlocked" from "process healthy". Probes return plain <c>Healthy</c>/<c>Unhealthy</c>
    /// text — no diagnostic payload — so an attacker who reaches the endpoints externally learns only that the
    /// container is alive (already detectable via port scan).
    /// </summary>
    /// <remarks>
    /// <para><c>/alive</c> = liveness. Runs only checks tagged <c>"live"</c> — fast, no I/O. ACA restarts the
    /// container when this fails. Keep cheap: a single in-process self-check.</para>
    /// <para><c>/health</c> = readiness. Runs every registered check (incl. SQL-server ping added by Aspire's
    /// <c>AddSqlServerDbContext</c>). ACA gates traffic on this. Slow checks are OK here.</para>
    /// <para>For a future internal-only diagnostic surface (per-check details, dependency latencies, etc.) we'd
    /// add a separate authenticated <c>/diag</c> endpoint — out of scope for v1.</para>
    /// </remarks>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(HealthEndpointPath);

        app.MapHealthChecks(AlivenessEndpointPath, new()
        {
            Predicate = r => r.Tags.Contains("live"),
        });

        return app;
    }
}
