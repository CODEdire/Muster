namespace Muster.Domain.Entities.Operations;

/// <summary>A registered external connector (e.g. a "Coin" loot system) authorized to call the API.</summary>
public class ApiClient
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Hash of the API key; the raw key is shown once at creation and never stored.</summary>
    public string ApiKeyHash { get; set; } = string.Empty;

    /// <summary>Granted scopes (e.g. read:ledger, write:currency) — what the <i>token</i> may call.</summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>The Discord user this key <i>acts as</i> (a bot/service account or a designated member, set by an
    /// admin). Actor-bound actions run with this user's identity + guild roles, so what the key can actually do is
    /// the intersection of its scopes and that user's permissions. 0 = unbound (can't perform actor-bound actions).</summary>
    public ulong ActsAsUserId { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}
