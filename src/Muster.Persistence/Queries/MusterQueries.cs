using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities.Musters;
using Muster.Domain.Enums;

namespace Muster.Persistence.Queries;

/// <summary>A muster linked to a session, with everything session-close needs: its check-in roster, its bonus
/// reward (points + coins), and its current status (for auto-close).</summary>
public record SessionLinkedMuster(Guid Id, long Points, long Coins, Guid? CoinCurrencyId, string Prompt, MusterStatus Status, int MinCheckIns, HashSet<ulong> Roster);

/// <summary>A posted muster card whose muster has gone terminal (Closed/Expired/Cancelled) — a cleanup candidate.
/// <see cref="RetentionHours"/> is the muster's own snapshot, so cleanup doesn't re-read guild settings.</summary>
public record MusterBoardCard(Guid PostedMessageId, ulong GuildId, ulong ChannelId, ulong MessageId, DateTimeOffset TerminalAt, int RetentionHours);

/// <summary>An open, non-linked muster past its expiry window — due to be auto-expired (and paid out).</summary>
public record MusterDueExpiry(Guid Id, ulong GuildId);

/// <summary>Queries over reaction musters.</summary>
public static class MusterQueries
{
    /// <summary>A muster (with its participants and session links) by id.</summary>
    public static Task<ReactionMuster?> FindMusterAsync(this MusterDbContext db, Guid musterId, CancellationToken ct = default)
        => db.ReactionMusters
            .Include(m => m.Participants)
            .Include(m => m.SessionLinks)
            .FirstOrDefaultAsync(m => m.Id == musterId, ct);

    /// <summary>The set of user ids checked in to any muster linked to <paramref name="sessionId"/>. Used at session
    /// close to gate the spendable-coin mint: a linked session mints coin only to attendees in this set. Empty set
    /// (no linked muster) means "not gated" — callers should treat no links as "mint for everyone" upstream.</summary>
    public static async Task<HashSet<ulong>> CheckedInUserIdsForSessionAsync(
        this MusterDbContext db, Guid sessionId, CancellationToken ct = default)
    {
        var ids = await db.MusterSessionLinks
            .Where(l => l.SessionId == sessionId)
            .SelectMany(l => l.Muster!.Participants.Select(p => p.UserId))
            .Distinct()
            .ToListAsync(ct);
        return [.. ids];
    }

    /// <summary>Whether <paramref name="sessionId"/> has at least one muster linked to it.</summary>
    public static Task<bool> SessionHasMusterAsync(this MusterDbContext db, Guid sessionId, CancellationToken ct = default)
        => db.MusterSessionLinks.AnyAsync(l => l.SessionId == sessionId, ct);

    /// <summary>Each muster linked to <paramref name="sessionId"/> with its roster, bonus reward, and status — for
    /// the session-close pass (coin gating, per-muster bonus, auto-close). Empty list = no linked muster.</summary>
    public static async Task<List<SessionLinkedMuster>> LinkedMustersForSessionAsync(
        this MusterDbContext db, Guid sessionId, CancellationToken ct = default)
    {
        var rows = await db.MusterSessionLinks
            .Where(l => l.SessionId == sessionId)
            .Select(l => new
            {
                l.Muster!.Id,
                l.Muster.Points,
                l.Muster.Coins,
                l.Muster.CoinCurrencyId,
                l.Muster.Prompt,
                l.Muster.Status,
                l.Muster.MinCheckIns,
                Users = l.Muster.Participants.Select(p => p.UserId).ToList(),
            })
            .ToListAsync(ct);

        return rows
            .Select(r => new SessionLinkedMuster(r.Id, r.Points, r.Coins, r.CoinCurrencyId, r.Prompt, r.Status, r.MinCheckIns, new HashSet<ulong>(r.Users)))
            .ToList();
    }

    /// <summary>Open, non-linked musters past their <c>ExpiresAt</c> — the sweep auto-expires (and pays out) these.
    /// Linked musters are excluded: they live with their session and are closed/paid at session close.</summary>
    public static Task<List<MusterDueExpiry>> ListDueExpiredMustersAsync(this MusterDbContext db, DateTimeOffset now, CancellationToken ct = default)
        => db.ReactionMusters
            .Where(m => m.Status == MusterStatus.Open && m.ExpiresAt != null && m.ExpiresAt <= now && !m.SessionLinks.Any())
            .Select(m => new MusterDueExpiry(m.Id, m.GuildId))
            .ToListAsync(ct);

    /// <summary>Open, <b>linked</b> musters past their <c>ExpiresAt</c> — the sweep soft-closes (Locks) these so they
    /// stop taking check-ins; they're paid + closed when their session ends. (Standalone ones go through
    /// <see cref="ListDueExpiredMustersAsync"/> instead.)</summary>
    public static Task<List<MusterDueExpiry>> ListDueLockMustersAsync(this MusterDbContext db, DateTimeOffset now, CancellationToken ct = default)
        => db.ReactionMusters
            .Where(m => m.Status == MusterStatus.Open && m.ExpiresAt != null && m.ExpiresAt <= now && m.SessionLinks.Any())
            .Select(m => new MusterDueExpiry(m.Id, m.GuildId))
            .ToListAsync(ct);

    /// <summary>Posted muster cards whose muster is terminal — the bot prunes the stale ones (older than the guild's
    /// retention) so closed musters don't linger in the channel. The muster + roster + ledger stay in the DB.</summary>
    public static Task<List<MusterBoardCard>> ListTerminalMusterBoardCardsAsync(this MusterDbContext db, CancellationToken ct = default)
        => db.PostedMessages
            .Where(p => p.EntityType == "muster")
            .Join(db.ReactionMusters, p => p.EntityId, m => m.Id, (p, m) => new { p, m })
            // Locked is soft-closed, not terminal — its card stays until the session closes it. Only truly terminal cards prune.
            .Where(x => x.m.Status != MusterStatus.Open && x.m.Status != MusterStatus.Locked)
            .Select(x => new MusterBoardCard(x.p.Id, x.p.GuildId, x.p.ChannelId, x.p.MessageId, x.m.ClosedAt ?? x.m.CreatedAt, x.m.RetentionHours))
            .ToListAsync(ct);
}
