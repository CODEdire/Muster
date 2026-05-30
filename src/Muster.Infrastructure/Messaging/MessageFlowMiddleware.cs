using Microsoft.Extensions.Logging;
using Muster.Contracts;
using Wolverine;

namespace Muster.Infrastructure.Messaging;

/// <summary>
/// Diagnostic middleware that logs receive-side activity for every cross-host message (the ones listed
/// in <see cref="MessageRouting.Contracts"/>). Lets you watch the bot/web consoles and see each
/// QuestLifecycleNotified / CurrencyMovementRecorded / SyncGuildMembers / SessionAttendanceChanged
/// arrive at its subscriber. Attached only to chains whose message type is cross-host so it doesn't
/// add noise to local-only command dispatch.
///
/// Publish-side activity is logged by Wolverine itself at Information level — bump the <c>Wolverine</c>
/// log namespace if you want to see "Sending message X to topic Y" lines too (default appsettings
/// already keeps it at Information).
/// </summary>
public static class MessageFlowMiddleware
{
    public static void Before(Envelope envelope, ILogger<MessageFlowMarker> logger)
    {
        var type = envelope.Message?.GetType().Name ?? "<null>";
        logger.LogInformation("← RECV cross-host {Type} (id={Id}, attempts={Attempts})", type, envelope.Id, envelope.Attempts);
    }

    /// <summary>Runs only on successful handler completion. Failures are surfaced by Wolverine's own
    /// runtime exception logger (which captures the real exception + stack and writes at Error level
    /// under the <c>Wolverine</c> log category).</summary>
    public static void After(Envelope envelope, ILogger<MessageFlowMarker> logger)
    {
        var type = envelope.Message?.GetType().Name ?? "<null>";
        logger.LogInformation("✓ DONE cross-host {Type} (id={Id})", type, envelope.Id);
    }
}

/// <summary>Marker type only used as the generic for <see cref="ILogger{TCategoryName}"/> so the
/// flow-logger lines all show up under the same readable category (<c>Muster.Infrastructure.Messaging.MessageFlowMarker</c>).</summary>
public sealed class MessageFlowMarker;
