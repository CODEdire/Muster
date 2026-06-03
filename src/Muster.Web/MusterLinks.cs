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

    /// <summary>The Sessions board — member view (Tracking group, mirrors the Musters board).</summary>
    public static string Sessions(ulong guildId) => $"/guilds/{guildId}/guild/tracking/sessions";

    /// <summary>One session's detail page.</summary>
    public static string SessionDetail(ulong guildId, Guid sessionId) => $"/guilds/{guildId}/guild/tracking/sessions/{sessionId}";

    /// <summary>Start a new tracking session.</summary>
    public static string SessionNew(ulong guildId) => $"/guilds/{guildId}/guild/tracking/sessions/new";

    /// <summary>A session's attendance CSV export (file endpoint, same Tracking prefix).</summary>
    public static string SessionExport(ulong guildId, Guid sessionId) => $"/guilds/{guildId}/guild/tracking/sessions/{sessionId}/export.csv";

    /// <summary>The Musters board — member card view (Guild nav group).</summary>
    public static string Musters(ulong guildId) => $"/guilds/{guildId}/guild/musters";

    /// <summary>The staff manage grid (managers: all; creators: their own).</summary>
    public static string MusterManage(ulong guildId) => $"/guilds/{guildId}/guild/musters/manage";

    /// <summary>Post a new muster (optionally pre-linked to a session).</summary>
    public static string MusterNew(ulong guildId, Guid? sessionId = null) =>
        $"/guilds/{guildId}/guild/musters/new" + (sessionId is { } s ? $"?session={s}" : "");

    /// <summary>One muster's detail page.</summary>
    public static string MusterDetail(ulong guildId, Guid musterId) => $"/guilds/{guildId}/guild/musters/{musterId}";

    /// <summary>Edit a live muster.</summary>
    public static string MusterEdit(ulong guildId, Guid musterId) => $"/guilds/{guildId}/guild/musters/{musterId}/edit";

    /// <summary>Tracking settings (Management nav group, mirrors Muster settings).</summary>
    public static string TrackingSettings(ulong guildId) => $"/guilds/{guildId}/management/tracking";

    /// <summary>Muster settings (Management nav group).</summary>
    public static string MusterSettings(ulong guildId) => $"/guilds/{guildId}/management/musters";

    /// <summary>Muster templates list.</summary>
    public static string MusterTemplates(ulong guildId) => $"/guilds/{guildId}/management/musters/templates";

    /// <summary>New / edit a muster template.</summary>
    public static string MusterTemplateNew(ulong guildId) => $"/guilds/{guildId}/management/musters/templates/new";
    public static string MusterTemplateEdit(ulong guildId, Guid templateId) => $"/guilds/{guildId}/management/musters/templates/{templateId}/edit";
}
