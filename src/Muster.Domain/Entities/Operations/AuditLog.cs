namespace Muster.Domain.Entities.Operations;

/// <summary>Record of an administrative or configuration action taken via the web UI.</summary>
public class AuditLog
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong ActorUserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Details { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
