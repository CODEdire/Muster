using Muster.Contracts;
using Muster.Domain;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Quests;
using NetCord;
using NetCord.Rest;

namespace Muster.Bot.Questing.Rendering;

/// <summary>
/// Renders a quest into a Discord embed for the channel board — the bot's analogue of the web card. Pure (no
/// Discord/IO) so it's unit-testable: a <see cref="QuestDetailView"/> in, an <see cref="EmbedProperties"/> out.
/// Colour + author badge echo the web card's tier edge; emoji stand in for per-field colour (an embed has a single
/// accent bar). Participant mentions render as names without pinging — Discord never notifies from embed content.
/// </summary>
public static class QuestEmbedRenderer
{
    /// <summary><paramref name="includeNotes"/> controls whether participant/review notes and the dispute reason are
    /// shown — they can be personal, so the <b>public</b> board card omits them (passes false); mod-channel and DM
    /// cards include them.</summary>
    public static EmbedProperties Render(QuestDetailView q, ulong guildId, string? webBaseUrl = null, bool includeNotes = true)
    {
        var kind = q.Origin == QuestOrigin.Guild ? "Guild" : "Player";
        var url = WebUrl(webBaseUrl, guildId, q.Id);

        var fields = new List<EmbedFieldProperties>
        {
            new() { Name = "Type", Value = kind, Inline = true },
            new() { Name = "Status", Value = StatusLabel(q), Inline = true },
        };

        // Reward on its own line: currency, then the tier bonus POINTS on a second line (the tier itself is shown
        // by the colour + author, so it isn't repeated as a field). Emoji give the colour an embed field can't.
        var reward = $"🪙 {q.RewardAmount} {q.RewardCode}";
        if (q.Tier != QuestTier.None && q.BonusPoints > 0)
        {
            reward += $"\n✨ +{q.BonusPoints} {CurrencyCodes.PointsCode}";
        }

        fields.Add(new() { Name = "Reward", Value = reward, Inline = false });

        if (q.Capacity > 1)
        {
            fields.Add(new() { Name = "Slots", Value = $"{ActiveCount(q)}/{q.Capacity}", Inline = true });
        }

        var completions = q.Participants.Count(p => p.Status == QuestParticipantStatus.Approved);
        if (completions > 0)
        {
            fields.Add(new() { Name = "Completions", Value = $"✅ {completions}", Inline = true });
        }

        if (q.Status == QuestStatus.Scheduled && q.ScheduledStart is { } start)
        {
            fields.Add(new() { Name = "Opens", Value = $"<t:{start.ToUnixTimeSeconds()}:R>", Inline = true });
        }

        if (q.Deadline is { } d)
        {
            fields.Add(new() { Name = "Expires", Value = $"<t:{d.ToUnixTimeSeconds()}:R>", Inline = true });
        }

        if (q.Status == QuestStatus.Disputed && q.DisputedBy is { } disputer)
        {
            fields.Add(new() { Name = "Disputed by", Value = $"<@{disputer}>", Inline = true });
            if (includeNotes && !string.IsNullOrWhiteSpace(q.DisputeReason))
            {
                fields.Add(new() { Name = "Dispute reason", Value = Truncate(q.DisputeReason, 1000), Inline = false });
            }
        }

        var roster = q.Participants.Where(p => p.Status != QuestParticipantStatus.Released).ToList();
        if (roster.Count > 0)
        {
            var lines = roster.Take(12).Select(p =>
            {
                var by = p.ReviewedBy is { } reviewer ? $" · by <@{reviewer}>" : "";
                // Notes can be personal — only the mod/DM cards show them (the public board passes includeNotes:false).
                var note = !includeNotes ? ""
                    : !string.IsNullOrWhiteSpace(p.ReviewNote) ? $"\n   📝 {Truncate(p.ReviewNote, 120)}"
                    : !string.IsNullOrWhiteSpace(p.Note) ? $"\n   💬 {Truncate(p.Note, 120)}"
                    : "";
                return $"{ParticipantEmoji(p.Status)} <@{p.UserId}> — {ParticipantLabel(p.Status)}{by}{note}";
            });
            fields.Add(new() { Name = $"Participants ({roster.Count})", Value = Truncate(string.Join("\n", lines), 1024), Inline = false });
        }

        var embed = new EmbedProperties
        {
            Title = q.Name,
            Url = url,
            Description = string.IsNullOrWhiteSpace(q.Description) ? null : Truncate(q.Description, 1000),
            Color = TierColor(q.Tier),
            Fields = fields,
            Footer = Footer(q, webBaseUrl),
            Timestamp = q.CreatedAt,
        };

        // Tier badge as the author line (E–S), linking to the web page; icon is a web-hosted badge image.
        if (q.Tier != QuestTier.None)
        {
            embed.Author = new EmbedAuthorProperties
            {
                Name = $"Tier {q.Tier}",
                Url = url,
                IconUrl = webBaseUrl is null ? null : $"{webBaseUrl.TrimEnd('/')}/img/tiers/{q.Tier.ToString().ToLowerInvariant()}.png",
            };
        }

        return embed;
    }

    /// <summary>
    /// A compact, button-less outcome notice DM'd to the person a terminal/outcome moment is about (paid, rejected,
    /// refunded, released…). Deliberately lighter than the full board card — a one-line headline + the detail that
    /// matters (e.g. how much you earned), with a link back to the web page for the rest.
    /// </summary>
    public static EmbedProperties RenderOutcome(QuestDetailView q, ulong guildId, QuestLifecycleMoment moment, string? webBaseUrl = null)
    {
        var (emoji, heading, color) = Outcome(moment);
        var body = $"**{q.Name}**";
        if (OutcomeDetail(q, moment) is { } detail)
        {
            body += $"\n{detail}";
        }

        return new EmbedProperties
        {
            Title = $"{emoji} {heading}",
            Url = WebUrl(webBaseUrl, guildId, q.Id),
            Description = Truncate(body, 1000),
            Color = color,
            Footer = Footer(q, webBaseUrl),
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>A compact "closes soon" nudge DM'd before a quest's deadline. <paramref name="ownerNoTaker"/> tailors the
    /// copy for a bounty owner whose quest is about to expire unclaimed (vs. a worker who still needs to submit).</summary>
    public static EmbedProperties RenderDeadlineReminder(QuestDetailView q, ulong guildId, bool ownerNoTaker, string? webBaseUrl = null)
    {
        var when = q.Deadline is { } d ? $"<t:{d.ToUnixTimeSeconds()}:R> (<t:{d.ToUnixTimeSeconds()}:f>)" : "soon";
        var line = ownerNoTaker
            ? $"**{q.Name}** closes {when} with no taker yet — extend the deadline or cancel to refund your escrow."
            : $"**{q.Name}** closes {when} — submit your work before then so it isn't expired.";

        return new EmbedProperties
        {
            Title = "⏰ Quest closing soon",
            Url = WebUrl(webBaseUrl, guildId, q.Id),
            Description = Truncate(line, 1000),
            Color = new Color(0xF59E0B), // amber — time-sensitive
            Footer = Footer(q, webBaseUrl),
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    private static (string Emoji, string Heading, Color Color) Outcome(QuestLifecycleMoment m) => m switch
    {
        QuestLifecycleMoment.Settled => ("✅", "Quest complete — reward paid", new Color(0x22C55E)),
        QuestLifecycleMoment.Rejected => ("❌", "Submission rejected", new Color(0xEF4444)),
        QuestLifecycleMoment.RejectedAtIntake => ("🚫", "Quest rejected at intake", new Color(0x4B5563)),
        QuestLifecycleMoment.Refunded => ("↩️", "Refunded", new Color(0x3B82F6)),
        QuestLifecycleMoment.Released => ("⏳", "Claim released", new Color(0x4B5563)),
        QuestLifecycleMoment.Reopened => ("🔁", "Submission reopened", new Color(0x3B82F6)),
        _ => ("ℹ️", "Quest update", new Color(0x4B5563)),
    };

    private static string? OutcomeDetail(QuestDetailView q, QuestLifecycleMoment m) => m switch
    {
        QuestLifecycleMoment.Settled => $"You earned 🪙 {q.RewardAmount} {q.RewardCode}"
            + (q.Tier != QuestTier.None && q.BonusPoints > 0 ? $" and ✨ +{q.BonusPoints} {CurrencyCodes.PointsCode}" : "") + ".",
        QuestLifecycleMoment.Rejected => "No reward this time — see the reviewer's note on the board.",
        QuestLifecycleMoment.RejectedAtIntake => "Your escrow has been refunded.",
        QuestLifecycleMoment.Refunded => "Your escrowed reward has been returned.",
        QuestLifecycleMoment.Released => "Your slot is open again — re-claim if you want back in.",
        QuestLifecycleMoment.Reopened => "Your submission is back under review.",
        _ => null,
    };

    // Footer mirrors the web card's issuer line: a guild quest shows the guild shield, a player quest the owner's avatar.
    private static EmbedFooterProperties Footer(QuestDetailView q, string? webBaseUrl) => q.Origin == QuestOrigin.Guild
        ? new EmbedFooterProperties { Text = "Guild issued", IconUrl = webBaseUrl is null ? null : $"{webBaseUrl.TrimEnd('/')}/img/tiers/guild.png" }
        : new EmbedFooterProperties { Text = q.OwnerName, IconUrl = q.OwnerAvatar };

    private static string? WebUrl(string? baseUrl, ulong guildId, Guid questId) =>
        string.IsNullOrWhiteSpace(baseUrl) ? null : $"{baseUrl.TrimEnd('/')}/guilds/{guildId}/quests/{questId}";

    /// <summary>Members actively on the quest (claimed/submitted/in-revision/approved) — the "X/Y slots" numerator.</summary>
    private static int ActiveCount(QuestDetailView q) =>
        q.Participants.Count(p => p.Status is QuestParticipantStatus.Claimed or QuestParticipantStatus.Submitted
            or QuestParticipantStatus.RevisionRequested or QuestParticipantStatus.Approved);

    private static bool HasActiveWorker(QuestDetailView q) =>
        q.Participants.Any(p => p.Status is QuestParticipantStatus.Claimed or QuestParticipantStatus.Submitted
            or QuestParticipantStatus.RevisionRequested);

    /// <summary>Status with a colour-cue emoji (an embed has one accent bar, so colour per state lives in emoji).</summary>
    private static string StatusLabel(QuestDetailView q) => q.Status switch
    {
        QuestStatus.Open when HasActiveWorker(q) => "🟡 In progress",
        QuestStatus.Open => "🟢 Open",
        QuestStatus.Scheduled => "🕒 Scheduled",
        QuestStatus.PendingApproval => "🟠 Pending intake",
        QuestStatus.PendingFinal => "🟠 Awaiting sign-off",
        QuestStatus.Disputed => "⚔️ Disputed",
        QuestStatus.Closed => "✅ Closed",
        QuestStatus.Cancelled => "🚫 Cancelled",
        QuestStatus.Expired => "⌛ Expired",
        _ => q.Status.ToString(),
    };

    private static string ParticipantEmoji(QuestParticipantStatus s) => s switch
    {
        QuestParticipantStatus.Approved => "✅",
        QuestParticipantStatus.Submitted => "📨",
        QuestParticipantStatus.RevisionRequested => "✏️",
        QuestParticipantStatus.Rejected => "❌",
        _ => "⏳",
    };

    private static string ParticipantLabel(QuestParticipantStatus s) => s switch
    {
        QuestParticipantStatus.RevisionRequested => "Revision",
        _ => s.ToString(),
    };

    /// <summary>Tier accent colour — matches the web card's edge (the deeper end of its gradient). Untiered quests
    /// get a neutral charcoal (distinct from tier E's lighter slate); origin is conveyed by the Type field/footer.</summary>
    private static Color TierColor(QuestTier tier) => tier switch
    {
        QuestTier.S => new Color(0xF59E0B), // amber
        QuestTier.A => new Color(0xEF4444), // red
        QuestTier.B => new Color(0x8B5CF6), // purple
        QuestTier.C => new Color(0x3B82F6), // blue
        QuestTier.D => new Color(0x22C55E), // green
        QuestTier.E => new Color(0x94A3B8), // slate
        _ => new Color(0x4B5563),           // no tier — neutral charcoal grey
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
