using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Discord;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Web.Components.Shared.Audit;

/// <summary>Resolved chip data for a user reference inside an audit token list.</summary>
public record AuditUserInfo(ulong Id, string Name, string? AvatarUrl);

/// <summary>Per-page entity cache for audit token rendering. Formatters emit raw ids; the page calls
/// <see cref="LoadAsync"/> once with every rendered token list, which batches the lookups by kind. The renderer
/// then resolves each token synchronously via <see cref="GetUser"/>/etc.
///
/// Phase A only batches user references — role/channel/currency follow when their first formatter lands.</summary>
public class AuditLookups(MusterDbContext db)
{
    private readonly Dictionary<ulong, AuditUserInfo> _users = [];

    public AuditUserInfo? GetUser(ulong id) => _users.GetValueOrDefault(id);

    /// <summary>Scan every token across every rendered row, dedup ids, run one batch per kind, populate the cache.
    /// Safe to call multiple times — subsequent calls only add the new ids. Pass extra user ids alongside the tokens
    /// to fold in the entries' Target column (rendered outside the formatter token stream).</summary>
    public async Task LoadAsync(IEnumerable<AuditRender> renders, IEnumerable<ulong>? extraUserIds = null, CancellationToken ct = default)
    {
        var allTokens = renders
            .SelectMany(r => r.Summary.Concat(r.Sections.SelectMany(s => s.Body)))
            .ToList();

        var userIds = allTokens
            .OfType<UserToken>()
            .Select(t => t.Id)
            .Concat(extraUserIds ?? [])
            .Distinct()
            .Where(id => !_users.ContainsKey(id))
            .ToList();

        if (userIds.Count > 0)
        {
            var map = await db.UserDisplayMapAsync(userIds, ct);
            foreach (var (id, (name, hash)) in map)
            {
                _users[id] = new AuditUserInfo(id, name, DiscordCdn.AvatarUrl(id, hash));
            }
        }
    }
}
