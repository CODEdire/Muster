using Muster.Contracts;

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

    /// <summary>When true, opening a tracking session auto-creates + links a check-in muster. Each session open may
    /// override it.</summary>
    public bool AutoCreateOnSession { get; set; }

    /// <summary>When true, the creator is automatically checked into a muster they create (handy for linked sessions
    /// where the host is also attending). Turn off when staff usually create musters for others.</summary>
    public bool CreatorAutoCheckIn { get; set; } = true;

    /// <summary>Default max active time (hours) before a muster auto-closes, so stale musters don't linger. 0 = no
    /// expiry. A template's own expiry overrides this; a standalone muster expires (and pays), a linked one
    /// soft-closes (Locked) and pays at session close. Templates/per-create values override.</summary>
    public int DefaultExpiryHours { get; set; }

    /// <summary>Gate mode applied to a session's coin when a muster is auto-created for it on open (Any = checking
    /// into the muster suffices). Default <see cref="SessionCoinGate.Any"/>.</summary>
    public SessionCoinGate AutoCreateGate { get; set; } = SessionCoinGate.Any;

    /// <summary>Where an auto-created muster posts: the default muster channel, or the session's own channel (when
    /// the allow-list permits it, else falls back to the default).</summary>
    public MusterAutoCreateChannel AutoCreateChannel { get; set; } = MusterAutoCreateChannel.DefaultChannel;

    // --- Global reward defaults (a muster with no template uses these; templates override per-creation) ---

    /// <summary>Baseline participation POINTS granted (at close) for checking into any muster. 0 = none.</summary>
    public long DefaultPoints { get; set; }

    /// <summary>Baseline spendable coins granted (at close) for checking into any muster. 0 = none.</summary>
    public long DefaultCoins { get; set; }

    /// <summary>Baseline minimum check-ins required before a muster pays out. null = no minimum; 0 is a valid
    /// (always-met) minimum. Templates/per-create values override.</summary>
    public int? DefaultMinCheckIns { get; set; }

    /// <summary>Currency the default coins are minted in (a spendable currency). Null = no coin reward by default.</summary>
    public Guid? DefaultCoinCurrencyId { get; set; }

    /// <summary>Channels a muster may post to. Empty (default) = any chat-capable channel; set to restrict.</summary>
    public List<ulong> AllowedChannelIds { get; set; } = [];

    /// <summary>Whether a muster may post to <paramref name="channelId"/>: always when no allow-list is set; otherwise
    /// only if listed. (0 = "fall back to the default channel" and is allowed; the default channel is validated when set.)</summary>
    public bool ChannelAllowed(ulong channelId)
        => channelId == 0 || AllowedChannelIds.Count == 0 || AllowedChannelIds.Contains(channelId);
}
