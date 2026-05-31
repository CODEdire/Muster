using Microsoft.EntityFrameworkCore;
using Muster.Contracts;
using Muster.Domain.Enums;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Services.Musters;

/// <summary>A session a muster is linked to (for the board's "Linked" column → session detail).</summary>
public record MusterListSession(Guid SessionId, string Name);

/// <summary>A muster row for the web admin list.</summary>
public record MusterListItem(
    Guid Id, string Display, MusterStatus Status, int CheckedIn, int? Capacity, int? MinCheckIns,
    long Points, long Coins, string? CoinCode, DateTimeOffset CreatedAt, ulong CreatedBy,
    IReadOnlyList<MusterListSession> Sessions);

/// <summary>At-a-glance counts for the muster board KPI cards.</summary>
public record MusterKpis(int Open, int Locked, int CheckedInOnOpen, int Linked, int Total);

/// <summary>A member-facing muster card (the web analogue of the Discord card): the general info plus whether the
/// viewing member is currently checked in. Used by the participant board — no roster, no admin fields.</summary>
public record MusterCard(
    Guid Id, string? Title, string Prompt, MusterStatus Status, int CheckedIn, int? Capacity, int? MinCheckIns,
    long Points, long Coins, string? CoinCode, bool YouCheckedIn, ulong CreatedBy,
    IReadOnlyList<MusterListSession> Sessions);

/// <summary>One muster's detail for the web admin page.</summary>
public record MusterDetailView(
    Guid Id, ulong GuildId, string? Title, string Prompt, MusterStatus Status,
    long Points, long Coins, string? CoinCode, int? Capacity, int? MinCheckIns, DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt, ulong CreatedBy, DateTimeOffset? ClosedAt, ulong ChannelId,
    IReadOnlyList<MusterParticipantView> Participants, IReadOnlyList<MusterLinkedSession> Sessions);

public record MusterParticipantView(ulong UserId, MusterParticipantSource Source, DateTimeOffset CheckedInAt);

public record MusterLinkedSession(Guid SessionId, string Name, TrackingSessionStatus Status, SessionCoinGate Gate);

/// <summary>Post-close summary of how a session's coin gate landed: of the attendees, how many qualified for the
/// gated coin vs were skipped (attended but didn't check into the required muster(s)). <see cref="HasGate"/> is
/// false when the session had no gating muster (everyone earned the coin).</summary>
public record SessionGateSummary(int Attendees, int CoinEligible, int Skipped, SessionCoinGate Gate, bool HasGate);

public interface IMusterReadService
{
    Task<IReadOnlyList<MusterListItem>> ListAsync(ulong guildId, bool includeClosed, CancellationToken ct = default);
    Task<MusterKpis> GetKpisAsync(ulong guildId, CancellationToken ct = default);

    /// <summary>Active musters (Open + Locked) as member-facing cards, flagged with whether <paramref name="userId"/>
    /// is checked in — for the participant board.</summary>
    Task<IReadOnlyList<MusterCard>> ActiveCardsAsync(ulong guildId, ulong userId, CancellationToken ct = default);

    Task<MusterDetailView?> GetDetailAsync(ulong guildId, Guid musterId, CancellationToken ct = default);

    /// <summary>Recent sessions a muster could be linked to (newest first) — for the link picker.</summary>
    Task<IReadOnlyList<MusterLinkedSession>> ListLinkableSessionsAsync(ulong guildId, CancellationToken ct = default);

    /// <summary>How a session's muster coin gate landed at close — for the "X earned the coin, Y skipped" notice.</summary>
    Task<SessionGateSummary?> SessionGateSummaryAsync(ulong guildId, Guid sessionId, CancellationToken ct = default);
}

/// <summary>Read-side projections for the web muster admin. Mutations go through the CQRS funnel (the bus), never here.</summary>
public class MusterReadService(MusterDbContext db) : IMusterReadService
{
    public async Task<IReadOnlyList<MusterListItem>> ListAsync(ulong guildId, bool includeClosed, CancellationToken ct = default)
    {
        var rows = await db.ReactionMusters
            .Where(m => m.GuildId == guildId && (includeClosed || m.Status == MusterStatus.Open))
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new
            {
                m.Id,
                Display = m.Title ?? m.Prompt,
                m.Status,
                Count = m.Participants.Count,
                m.Capacity,
                m.MinCheckIns,
                m.Points,
                m.Coins,
                Code = db.Currencies.Where(c => c.Id == m.CoinCurrencyId).Select(c => c.Code).FirstOrDefault(),
                m.CreatedAt,
                m.CreatedBy,
                SessionIds = m.SessionLinks.Select(l => l.SessionId).ToList(),
            })
            .ToListAsync(ct);

        // Resolve linked-session names in one round-trip, then stitch (MusterSessionLink has no session navigation).
        var sessionIds = rows.SelectMany(r => r.SessionIds).Distinct().ToList();
        var names = sessionIds.Count == 0
            ? []
            : await db.TrackingSessions.Where(s => sessionIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Name })
                .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        return rows.Select(r => new MusterListItem(
            r.Id, r.Display, r.Status, r.Count, r.Capacity, r.MinCheckIns, r.Points, r.Coins, r.Code,
            r.CreatedAt, r.CreatedBy,
            r.SessionIds.Select(id => new MusterListSession(id, names.GetValueOrDefault(id, "session"))).ToList()))
            .ToList();
    }

    public async Task<IReadOnlyList<MusterCard>> ActiveCardsAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var rows = await db.ReactionMusters
            .Where(m => m.GuildId == guildId && (m.Status == MusterStatus.Open || m.Status == MusterStatus.Locked))
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new
            {
                m.Id, m.Title, m.Prompt, m.Status, Count = m.Participants.Count, m.Capacity, m.MinCheckIns,
                m.Points, m.Coins, m.CreatedBy,
                Code = db.Currencies.Where(c => c.Id == m.CoinCurrencyId).Select(c => c.Code).FirstOrDefault(),
                YouCheckedIn = m.Participants.Any(p => p.UserId == userId),
                SessionIds = m.SessionLinks.Select(l => l.SessionId).ToList(),
            })
            .ToListAsync(ct);

        var sessionIds = rows.SelectMany(r => r.SessionIds).Distinct().ToList();
        var names = sessionIds.Count == 0
            ? []
            : await db.TrackingSessions.Where(s => sessionIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Name })
                .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        return rows.Select(r => new MusterCard(
            r.Id, r.Title, r.Prompt, r.Status, r.Count, r.Capacity, r.MinCheckIns, r.Points, r.Coins, r.Code,
            r.YouCheckedIn, r.CreatedBy,
            r.SessionIds.Select(id => new MusterListSession(id, names.GetValueOrDefault(id, "session"))).ToList()))
            .ToList();
    }

    public async Task<MusterKpis> GetKpisAsync(ulong guildId, CancellationToken ct = default)
    {
        var guild = db.ReactionMusters.Where(m => m.GuildId == guildId);
        var open = await guild.CountAsync(m => m.Status == MusterStatus.Open, ct);
        var locked = await guild.CountAsync(m => m.Status == MusterStatus.Locked, ct);
        var checkedIn = await guild.Where(m => m.Status == MusterStatus.Open).SelectMany(m => m.Participants).CountAsync(ct);
        var linked = await guild.CountAsync(m => m.SessionLinks.Any()
            && (m.Status == MusterStatus.Open || m.Status == MusterStatus.Locked), ct);
        var total = await guild.CountAsync(ct);
        return new MusterKpis(open, locked, checkedIn, linked, total);
    }

    public async Task<MusterDetailView?> GetDetailAsync(ulong guildId, Guid musterId, CancellationToken ct = default)
    {
        var muster = await db.ReactionMusters
            .Where(m => m.Id == musterId && m.GuildId == guildId)
            .Select(m => new
            {
                m.Id, m.GuildId, m.Title, m.Prompt, m.Status, m.Points, m.Coins, m.CoinCurrencyId,
                m.Capacity, m.MinCheckIns, m.ExpiresAt, m.CreatedAt, m.CreatedBy, m.ClosedAt, m.ChannelId,
                Code = db.Currencies.Where(c => c.Id == m.CoinCurrencyId).Select(c => c.Code).FirstOrDefault(),
                Participants = m.Participants
                    .OrderBy(p => p.CheckedInAt)
                    .Select(p => new MusterParticipantView(p.UserId, p.Source, p.CheckedInAt))
                    .ToList(),
                Sessions = m.SessionLinks
                    .Join(db.TrackingSessions, l => l.SessionId, s => s.Id, (l, s) => new MusterLinkedSession(s.Id, s.Name, s.Status, s.CoinGate))
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);

        if (muster is null)
        {
            return null;
        }

        return new MusterDetailView(
            muster.Id, muster.GuildId, muster.Title, muster.Prompt, muster.Status, muster.Points, muster.Coins, muster.Code,
            muster.Capacity, muster.MinCheckIns, muster.ExpiresAt, muster.CreatedAt, muster.CreatedBy, muster.ClosedAt, muster.ChannelId,
            muster.Participants, muster.Sessions);
    }

    public async Task<IReadOnlyList<MusterLinkedSession>> ListLinkableSessionsAsync(ulong guildId, CancellationToken ct = default)
        => await db.TrackingSessions
            .Where(s => s.GuildId == guildId)
            .OrderByDescending(s => s.StartedAt)
            .Take(50)
            .Select(s => new MusterLinkedSession(s.Id, s.Name, s.Status, s.CoinGate))
            .ToListAsync(ct);

    public async Task<SessionGateSummary?> SessionGateSummaryAsync(ulong guildId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await db.TrackingSessions
            .Where(s => s.Id == sessionId && s.GuildId == guildId)
            .Select(s => new { s.CoinGate })
            .FirstOrDefaultAsync(ct);
        if (session is null)
        {
            return null;
        }

        var attendees = await db.VoiceAttendance
            .Where(a => a.TrackingSessionId == sessionId && a.TotalMinutes > 0)
            .Select(a => a.UserId)
            .ToListAsync(ct);

        // Every assigned muster counts (under All it's required); only "no linked muster at all" is a good
        // completion that mints to everyone. Mirrors TrackingSessionService.CloseAsync.
        var rosters = await db.LinkedMustersForSessionAsync(sessionId, ct);
        var hasGate = session.CoinGate != SessionCoinGate.None && rosters.Count > 0;

        if (!hasGate)
        {
            return new SessionGateSummary(attendees.Count, attendees.Count, 0, session.CoinGate, false);
        }

        var qualifying = session.CoinGate == SessionCoinGate.All
            ? rosters.Select(m => new HashSet<ulong>(m.Roster)).Aggregate((acc, next) => { acc.IntersectWith(next); return acc; })
            : rosters.SelectMany(m => m.Roster).ToHashSet();

        var eligible = attendees.Count(qualifying.Contains);
        return new SessionGateSummary(attendees.Count, eligible, attendees.Count - eligible, session.CoinGate, true);
    }
}
