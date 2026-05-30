using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Muster.Bot.Platform.Telemetry;

/// <summary>
/// Bot-wide telemetry surfaces. One <see cref="ActivitySource"/> + one <see cref="Meter"/>, both named
/// <c>Muster.Bot</c>, so OTEL only has to register two strings on the tracing/metrics pipelines and every
/// surface lands in the same App Insights "Muster.Bot" cloud-role view.
///
/// <para>NetCord ships no OTEL hooks of its own, so the bot's user-facing surfaces (slash commands,
/// component interactions, autocomplete, gateway events, background services) all need explicit
/// instrumentation. The conventions encoded here keep them comparable in App Insights:
/// <list type="bullet">
/// <item><b>Activities</b>: <c>ActivityKind.Server</c> on the outermost span per invocation so App Insights
///   renders them on the Requests/Performance blade just like web HTTP routes. Names follow
///   <c>slash &lt;command&gt;</c> / <c>button &lt;feature&gt;:&lt;action&gt;</c> / <c>bg &lt;Service&gt;</c>.</item>
/// <item><b>Tags</b>: low-card facets (<c>command.name</c>, <c>result</c>, <c>discord.guild.id</c>) for
///   metrics + spans; high-card facets (<c>discord.user.id</c>, <c>discord.channel.id</c>) ONLY on spans —
///   never on metrics (each unique value = a time series).</item>
/// <item><b>Autocomplete</b>: counter + histogram only, no span — too high QPS for spans to be cheap.</item>
/// </list></para>
///
/// <para>See <c>docs/deployment.md</c> "Telemetry: Aspire Dashboard (run) vs App Insights (publish)" for
/// the end-to-end story.</para>
/// </summary>
public static class BotTelemetry
{
    public const string SourceName = "Muster.Bot";

    /// <summary>Single ActivitySource for every bot surface — register once in OTEL via
    /// <c>tracing.AddSource(BotTelemetry.SourceName)</c>.</summary>
    public static readonly ActivitySource Source = new(SourceName);

    /// <summary>Single Meter for every bot surface — register once in OTEL via
    /// <c>metrics.AddMeter(BotTelemetry.SourceName)</c>.</summary>
    public static readonly Meter Meter = new(SourceName);

    /// <summary>Counter for autocomplete invocations, tagged by provider + result. High-QPS — spans would
    /// dominate ingestion, so we drop to a counter (cheap aggregation).</summary>
    public static readonly Counter<long> AutocompleteCount = Meter.CreateCounter<long>(
        "muster.bot.autocomplete.count",
        unit: "{request}",
        description: "Autocomplete provider invocations.");

    /// <summary>Histogram of autocomplete duration in milliseconds, tagged by provider. Watch p99 here —
    /// Discord cancels autocomplete after ~3s and a slow provider degrades typing UX silently.</summary>
    public static readonly Histogram<double> AutocompleteDuration = Meter.CreateHistogram<double>(
        "muster.bot.autocomplete.duration",
        unit: "ms",
        description: "Autocomplete provider duration.");

    /// <summary>Starts the outermost Activity for a slash command. Tagged with low-card facets for the
    /// Performance blade plus high-card facets (user/channel) on the span only. Returns <c>null</c> if no
    /// OTEL listener is attached — callers <c>using</c>-dispose so the null case is a no-op.</summary>
    public static Activity? StartSlashCommand(string commandName, ulong guildId, ulong userId, ulong? channelId)
    {
        var activity = Source.StartActivity($"slash {commandName}", ActivityKind.Server);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("muster.surface", "slash");
        activity.SetTag("command.name", commandName);
        activity.SetTag("discord.guild.id", guildId);
        activity.SetTag("discord.user.id", userId);
        if (channelId is ulong cid)
        {
            activity.SetTag("discord.channel.id", cid);
        }

        return activity;
    }

    /// <summary>Starts the outermost Activity for a component interaction (button / select / modal).
    /// <paramref name="kind"/> is the surface label (e.g. "button"); <paramref name="actionKey"/> is the
    /// feature:action pair (e.g. "quest:claim").</summary>
    public static Activity? StartInteraction(string kind, string actionKey, ulong guildId, ulong userId, ulong? channelId)
    {
        var activity = Source.StartActivity($"{kind} {actionKey}", ActivityKind.Server);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("muster.surface", kind);
        activity.SetTag("interaction.action", actionKey);
        activity.SetTag("discord.guild.id", guildId);
        activity.SetTag("discord.user.id", userId);
        if (channelId is ulong cid)
        {
            activity.SetTag("discord.channel.id", cid);
        }

        return activity;
    }

    /// <summary>Starts the outermost Activity for a single background-service tick. Use:
    /// <code>using var activity = BotTelemetry.StartBackgroundTick(nameof(LedgerPruneScheduler));</code>
    /// inside the try-block of the existing PeriodicTimer loop.</summary>
    public static Activity? StartBackgroundTick(string serviceName)
    {
        var activity = Source.StartActivity($"bg {serviceName}", ActivityKind.Server);
        activity?.SetTag("muster.surface", "background");
        activity?.SetTag("service.name", serviceName);
        return activity;
    }

    /// <summary>Records the outcome of the current Activity. Sets <c>result</c> + status. Call inside the
    /// using-scope right before disposal so the tag lands on the span and on Application Insights as
    /// the request's <c>resultCode</c>/<c>success</c>.</summary>
    public static void SetResult(this Activity? activity, bool ok, string? reason = null)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("result", ok ? "ok" : "error");
        activity.SetStatus(ok ? ActivityStatusCode.Ok : ActivityStatusCode.Error, reason);
    }

    /// <summary>Records an exception against the current Activity + flags it as failed. Caller is expected
    /// to log/rethrow as appropriate — this only marks the span.</summary>
    public static void SetException(this Activity? activity, Exception ex)
    {
        if (activity is null)
        {
            return;
        }

        activity.AddException(ex);
        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.SetTag("result", "error");
        activity.SetTag("error.type", ex.GetType().FullName);
    }

    /// <summary>One-shot record of an autocomplete invocation. Use:
    /// <code>var sw = ValueStopwatch.StartNew(); var items = await GetItemsAsync(); BotTelemetry.RecordAutocomplete(nameof(X), sw.GetElapsedMs(), items.Count);</code>
    /// or wrap with <see cref="MeasureAutocomplete"/> which returns an <see cref="IDisposable"/>.</summary>
    public static void RecordAutocomplete(string provider, double durationMs, int itemCount)
    {
        var tags = new TagList { { "provider", provider } };
        AutocompleteCount.Add(1, tags);
        AutocompleteDuration.Record(durationMs, tags);
    }

    /// <summary>Disposable timer that records the autocomplete histogram + counter on dispose.</summary>
    public static AutocompleteMeasurement MeasureAutocomplete(string provider)
        => new(provider);

    public readonly struct AutocompleteMeasurement(string provider) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();

        public void Dispose()
        {
            var elapsed = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            var tags = new TagList { { "provider", provider } };
            AutocompleteCount.Add(1, tags);
            AutocompleteDuration.Record(elapsed, tags);
        }
    }
}
