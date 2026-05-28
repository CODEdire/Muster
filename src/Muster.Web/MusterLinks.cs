namespace Muster.Web;

/// <summary>Centralized URL builders for in-app navigation. One place to change a route template (and one place
/// for components like <c>UserChip</c> to ask "where does this member live?"). Keep additions terse — each method
/// is a single template, no business logic.</summary>
public static class MusterLinks
{
    /// <summary>The admin/officer MemberDetail page for one member of a guild.</summary>
    public static string MemberDetail(ulong guildId, ulong userId) => $"/guilds/{guildId}/members/{userId}";

    /// <summary>The Members directory.</summary>
    public static string Members(ulong guildId) => $"/guilds/{guildId}/members";

    /// <summary>The Sessions page.</summary>
    public static string Sessions(ulong guildId) => $"/guilds/{guildId}/sessions";

    /// <summary>One session's detail page.</summary>
    public static string SessionDetail(ulong guildId, Guid sessionId) => $"/guilds/{guildId}/sessions/{sessionId}";
}
