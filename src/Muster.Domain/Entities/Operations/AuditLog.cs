using Muster.Domain.Enums;

namespace Muster.Domain.Entities.Operations;

/// <summary>Record of an administrative or configuration action. <see cref="Origin"/> identifies where the action
/// was triggered (UI/Bot/Api/Connector/System); <see cref="TargetUserId"/> is the primary user the action affected
/// (null for guild-wide actions); <see cref="Outcome"/> records whether the operation succeeded; and
/// <see cref="CorrelationId"/> groups rows that belong to one logical operation (bulk awards, an HTTP request,
/// etc.) — pulled from the ambient OpenTelemetry activity when the call site doesn't set one.</summary>
public class AuditLog
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong ActorUserId { get; set; }
    public ulong? TargetUserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Details { get; set; }
    public AuditOrigin Origin { get; set; } = AuditOrigin.Unknown;
    public AuditOutcome Outcome { get; set; } = AuditOutcome.Success;
    /// <summary>OpenTelemetry trace id (hex), Wolverine envelope correlation id, or a caller-supplied GUID — a
    /// stable string that joins related rows. Max 64 chars (covers a 32-char OTel trace id with room for a prefix).</summary>
    public string? CorrelationId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
