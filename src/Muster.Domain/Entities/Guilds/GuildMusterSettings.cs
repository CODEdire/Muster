namespace Muster.Domain.Entities.Guilds;

/// <summary>
/// Per-guild muster configuration — its own table (the table-per-feature pilot, moved out of the <c>GuildSettings</c>
/// JSON blob). Flat columns for the scalars; <see cref="AllowedChannelIds"/> is a JSON column (a small, whole-read
/// list that won't change shape). This same type is also bound from configuration via <c>IOptions</c> to provide the
/// platform default values used to seed a guild's row the first time it's written.
/// </summary>
public class GuildMusterSettings
{
    /// <summary>Owning guild — primary key + FK (1:1 with <c>Guild</c>). 0 on the IOptions defaults template.</summary>
    public ulong GuildId { get; set; }

    /// <summary>Default channel muster cards post to (0 = post to the channel the command ran in). An author may
    /// override per-muster.</summary>
    public ulong MusterChannelId { get; set; }

    /// <summary>How long a terminal (closed/expired/cancelled) muster card lingers before cleanup deletes it.
    /// 0 = delete immediately. Web keeps full history regardless.</summary>
    public int BoardRetentionHours { get; set; } = 48;

    /// <summary>When true, opening a tracking session auto-creates + links a check-in muster (gate mode Any). Each
    /// session open may override it.</summary>
    public bool AutoCreateOnSession { get; set; }

    /// <summary>Channels a muster may post to. Empty (default) = any chat-capable channel; set to restrict.</summary>
    public List<ulong> AllowedChannelIds { get; set; } = [];

    /// <summary>Whether a muster may post to <paramref name="channelId"/>: always when no allow-list is set; otherwise
    /// only if listed. (0 = "fall back to the default channel" and is allowed; the default channel is validated when set.)</summary>
    public bool ChannelAllowed(ulong channelId)
        => channelId == 0 || AllowedChannelIds.Count == 0 || AllowedChannelIds.Contains(channelId);
}
