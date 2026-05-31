using Microsoft.EntityFrameworkCore;
using Muster.Contracts;
using Muster.Domain.Enums;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Services.Musters;

/// <summary>A muster row for the web admin list.</summary>
public record MusterListItem(
    Guid Id, string Display, MusterStatus Status, int CheckedIn, int? Capacity,
    long Reward, string? CurrencyCode, DateTimeOffset CreatedAt, ulong CreatedBy, int LinkedSessions);

/// <summary>One muster's detail for the web admin page.</summary>
public record MusterDetailView(
    Guid Id, ulong GuildId, string? Title, string Prompt, MusterStatus Status,
    long Reward, string? CurrencyCode, int? Capacity, DateTimeOffset? ExpiresAt,
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
        => await db.ReactionMusters
            .Where(m => m.GuildId == guildId && (includeClosed || m.Status == MusterStatus.Open))
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new MusterListItem(
                m.Id,
                m.Title ?? m.Prompt,
                m.Status,
                m.Participants.Count,
                m.Capacity,
                m.RewardAmount,
                db.Currencies.Where(c => c.Id == m.CurrencyId).Select(c => c.Code).FirstOrDefault(),
                m.CreatedAt,
                m.CreatedBy,
                m.SessionLinks.Count))
            .ToListAsync(ct);

    public async Task<MusterDetailView?> GetDetailAsync(ulong guildId, Guid musterId, CancellationToken ct = default)
    {
        var muster = await db.ReactionMusters
            .Where(m => m.Id == musterId && m.GuildId == guildId)
            .Select(m => new
            {
                m.Id, m.GuildId, m.Title, m.Prompt, m.Status, m.RewardAmount, m.CurrencyId,
                m.Capacity, m.ExpiresAt, m.CreatedAt, m.CreatedBy, m.ClosedAt, m.ChannelId,
                Code = db.Currencies.Where(c => c.Id == m.CurrencyId).Select(c => c.Code).FirstOrDefault(),
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
            muster.Id, muster.GuildId, muster.Title, muster.Prompt, muster.Status, muster.RewardAmount, muster.Code,
            muster.Capacity, muster.ExpiresAt, muster.CreatedAt, muster.CreatedBy, muster.ClosedAt, muster.ChannelId,
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
