namespace Muster.Domain.Entities;

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

/// <summary>A registered external connector (e.g. a "Coin" loot system) authorized to call the API.</summary>
public class ApiClient
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Hash of the API key; the raw key is shown once at creation and never stored.</summary>
    public string ApiKeyHash { get; set; } = string.Empty;

    /// <summary>Granted scopes (e.g. read:ledger, write:currency).</summary>
    public List<string> Scopes { get; set; } = [];

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}
