using Microsoft.Extensions.Configuration;

namespace Muster.Infrastructure.Platform;

/// <summary>
/// Builds absolute links into the Muster web app from the configured public base URL (<c>Web:BaseUrl</c>,
/// set by the AppHost tunnel). Used by the bot to turn slash-command replies into "open in web" deep links.
/// Every method returns <c>null</c> when no base URL is configured, so callers can degrade gracefully.
/// (Named to avoid colliding with ASP.NET Core's <c>Microsoft.AspNetCore.Routing.LinkGenerator</c>.)
/// </summary>
public class WebLinkBuilder(IConfiguration config)
{
    private readonly string? _baseUrl = config["Web:BaseUrl"]?.TrimEnd('/');

    /// <summary>True when a public base URL is configured (links can be produced).</summary>
    public bool HasBaseUrl => !string.IsNullOrWhiteSpace(_baseUrl);

    private string? Build(string relativePath) =>
        string.IsNullOrWhiteSpace(_baseUrl) ? null : $"{_baseUrl}/{relativePath.TrimStart('/')}";

    public string? Guild(ulong guildId) => Build($"guilds/{guildId}");

    /// <summary>The participation Sessions page for a guild.</summary>
    public string? Sessions(ulong guildId) => Build($"guilds/{guildId}/sessions");

    /// <summary>The "create a tracking session" web wizard for a guild.</summary>
    public string? NewSession(ulong guildId) => Build($"guilds/{guildId}/sessions/new");

    /// <summary>A single session's detail page.</summary>
    public string? SessionDetail(Guid sessionId) => Build($"sessions/{sessionId}");

    /// <summary>The tracking settings (background channels + policy) page for a guild.</summary>
    public string? TrackingSettings(ulong guildId) => Build($"guilds/{guildId}/tracking");

    /// <summary>The member's personal dashboard.</summary>
    public string? Me() => Build("me");

    /// <summary>The member's wallet.</summary>
    public string? Wallet() => Build("wallet");
}
