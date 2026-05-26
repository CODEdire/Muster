using Muster.Contracts;
using Muster.Infrastructure.Services.Platform;
using Wolverine;

namespace Muster.Infrastructure.Messaging;

/// <summary>
/// Wolverine middleware that records the audit trail for every successful guild command — the one place
/// auditing happens, so adapters (bot/web/api) don't each re-implement it. Registered only against handler
/// chains whose message is an <see cref="IGuildCommand"/>; runs after the handler and records when the
/// command succeeded. Action = the command type name; details = the command itself. The message is read off
/// the <see cref="Envelope"/> (Wolverine can't bind the concrete message to the interface as a variable).
/// </summary>
public static class AuditMiddleware
{
    public static async Task After(Result result, Envelope envelope, AuditService audit, CancellationToken ct)
    {
        if (result.Ok && envelope.Message is IGuildCommand command)
        {
            await audit.RecordAsync(command.GuildId, command.ActorId, command.GetType().Name, command.ToString(), ct);
        }
    }
}
