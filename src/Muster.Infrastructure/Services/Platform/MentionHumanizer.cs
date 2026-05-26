using System.Text.RegularExpressions;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Services.Platform;

/// <summary>
/// Turns Discord mention tokens — which the bot uses because Discord renders them — into readable names
/// for the web UI. <c>&lt;@&amp;id&gt;</c> → role name, <c>&lt;@id&gt;</c> → member display name.
/// </summary>
public partial class MentionHumanizer(MusterDbContext db)
{
    [GeneratedRegex(@"<@&(\d+)>")]
    private static partial Regex RoleMention();

    [GeneratedRegex(@"<@!?(\d+)>")]
    private static partial Regex UserMention();

    public async Task<string> HumanizeAsync(ulong guildId, string? text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var roleIds = RoleMention().Matches(text).Select(m => ulong.Parse(m.Groups[1].Value)).Distinct().ToList();
        var userIds = UserMention().Matches(text).Select(m => ulong.Parse(m.Groups[1].Value)).Distinct().ToList();

        var roleNames = roleIds.Count == 0
            ? []
            : await db.RoleNameMapAsync(guildId, roleIds, ct);

        var userNames = userIds.Count == 0
            ? []
            : await db.UserDisplayNameMapAsync(userIds, ct);

        text = RoleMention().Replace(text, m =>
            "@" + (roleNames.GetValueOrDefault(ulong.Parse(m.Groups[1].Value)) ?? m.Groups[1].Value));

        text = UserMention().Replace(text, m =>
            "@" + (userNames.GetValueOrDefault(ulong.Parse(m.Groups[1].Value)) ?? m.Groups[1].Value));

        return text;
    }
}
