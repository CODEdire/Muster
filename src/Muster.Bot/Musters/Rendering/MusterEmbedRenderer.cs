using Muster.Domain.Entities.Musters;
using Muster.Domain.Enums;
using NetCord;
using NetCord.Rest;

namespace Muster.Bot.Musters.Rendering;

/// <summary>
/// Renders a muster into a Discord embed for the channel card — the bot's analogue of the web view. Pure (no
/// Discord/IO) so it's unit-testable. The roster shows participant mentions as names without pinging (Discord never
/// notifies from embed content).
/// </summary>
public static class MusterEmbedRenderer
{
    public static EmbedProperties Render(ReactionMuster muster, string? currencyCode, string? webBaseUrl = null)
    {
        var checkedIn = muster.Participants.Count;
        var countValue = muster.Capacity is { } cap && muster.SessionLinks.Count == 0
            ? $"✅ {checkedIn}/{cap}"
            : $"✅ {checkedIn}";

        var fields = new List<EmbedFieldProperties>
        {
            new() { Name = "Status", Value = StatusLabel(muster.Status), Inline = true },
            new() { Name = "Checked in", Value = countValue, Inline = true },
        };

        if (muster.RewardAmount > 0)
        {
            fields.Add(new() { Name = "Reward", Value = $"🪙 {muster.RewardAmount} {currencyCode ?? "POINTS"}", Inline = true });
        }

        if (muster.Status == MusterStatus.Open && muster.ExpiresAt is { } expiry)
        {
            fields.Add(new() { Name = "Closes", Value = $"<t:{expiry.ToUnixTimeSeconds()}:R>", Inline = true });
        }

        var roster = muster.Participants.OrderBy(p => p.CheckedInAt).ToList();
        if (roster.Count > 0)
        {
            var lines = roster.Take(40).Select(p => $"<@{p.UserId}>");
            var more = roster.Count > 40 ? $" …and {roster.Count - 40} more" : "";
            fields.Add(new() { Name = $"Members ({roster.Count})", Value = Truncate(string.Join(", ", lines) + more, 1024), Inline = false });
        }

        return new EmbedProperties
        {
            Title = string.IsNullOrWhiteSpace(muster.Title) ? "📋 Muster — check in" : $"📋 {muster.Title}",
            Url = WebUrl(webBaseUrl, muster.GuildId, muster.Id),
            Description = Truncate(muster.Prompt, 2000),
            Color = StatusColor(muster.Status),
            Fields = fields,
            Timestamp = muster.CreatedAt,
        };
    }

    private static string StatusLabel(MusterStatus s) => s switch
    {
        MusterStatus.Open => "🟢 Open",
        MusterStatus.Closed => "✅ Closed",
        MusterStatus.Expired => "⌛ Expired",
        MusterStatus.Cancelled => "🚫 Cancelled",
        _ => s.ToString(),
    };

    private static Color StatusColor(MusterStatus s) => s switch
    {
        MusterStatus.Open => new Color(0x22C55E),    // green
        MusterStatus.Closed => new Color(0x3B82F6),  // blue
        MusterStatus.Expired => new Color(0x94A3B8), // slate
        _ => new Color(0x4B5563),                    // charcoal
    };

    private static string? WebUrl(string? baseUrl, ulong guildId, Guid musterId) =>
        string.IsNullOrWhiteSpace(baseUrl) ? null : $"{baseUrl.TrimEnd('/')}/guilds/{guildId}/musters/{musterId}";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..Math.Max(0, max - 1)] + "…";
}
