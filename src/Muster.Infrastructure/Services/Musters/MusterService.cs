using System.Data;
using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Entities.Musters;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;

namespace Muster.Infrastructure.Services.Musters;

public enum ReactionOutcome
{
    Recorded,
    AlreadyParticipated,
    Expired,
    Closed,
    Full,
    NotFound,
    NotEligible,
}

/// <summary>
/// Reaction "muster" check-ins: create the post record, record who checks in (via the button), and let staff curate
/// the roster. <b>Rewards are paid at close, never on check-in</b> — a non-linked muster pays its roster when the
/// muster itself closes (<see cref="CloseAsync"/>); a linked muster pays at session close, gated on attendance
/// (<c>TrackingSessionService</c>). So removing a participant before close simply drops them — there is nothing to
/// reverse. Check-in is idempotent per (muster, user). A linked muster ignores its own capacity.
/// </summary>
public class MusterService(MusterDbContext db, ICurrencyService awards, GuildAuthorizationService auth)
{
    public async Task<ReactionMuster> CreateAsync(
        ulong guildId, ulong channelId, string? title, string prompt, Guid currencyId, long rewardAmount,
        int? capacity, DateTimeOffset? expiresAt, ulong createdBy,
        IEnumerable<string>? emojis = null, Guid? sessionId = null, CancellationToken ct = default)
    {
        var muster = new ReactionMuster
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            ChannelId = channelId,
            Title = title,
            Prompt = prompt,
            Emojis = emojis?.ToList() ?? [],
            CurrencyId = currencyId,
            RewardAmount = rewardAmount,
            Capacity = capacity,
            ExpiresAt = expiresAt,
            Status = MusterStatus.Open,
            CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        if (sessionId is { } sid)
        {
            muster.SessionLinks.Add(new MusterSessionLink { MusterId = muster.Id, SessionId = sid });
        }

        db.ReactionMusters.Add(muster);
        await db.SaveChangesAsync(ct);
        return muster;
    }

    /// <summary>Create a muster that rewards the guild's POINTS currency.</summary>
    public async Task<ReactionMuster> CreatePointsAsync(
        ulong guildId, ulong channelId, string? title, string prompt, long rewardPoints,
        int? capacity, DateTimeOffset? expiresAt, ulong createdBy,
        IEnumerable<string>? emojis = null, Guid? sessionId = null, CancellationToken ct = default)
    {
        var points = await db.FindPointsAsync(guildId, ct)
            ?? throw new InvalidOperationException($"POINTS currency not provisioned for guild {guildId}.");

        return await CreateAsync(guildId, channelId, title, prompt, points.Id, rewardPoints, capacity, expiresAt, createdBy, emojis, sessionId, ct);
    }

    /// <summary>Record a member's check-in (no reward — that's paid at close). Idempotent per (muster, user).
    /// <paramref name="source"/> <see cref="MusterParticipantSource.Admin"/> is a staff override that bypasses the
    /// eligibility + capacity gates.</summary>
    public async Task<ReactionOutcome> CheckInAsync(
        Guid musterId, ulong userId, MusterParticipantSource source, CancellationToken ct = default)
    {
        var muster = await db.FindMusterAsync(musterId, ct);
        if (muster is null)
        {
            return ReactionOutcome.NotFound;
        }

        var isAdmin = source == MusterParticipantSource.Admin;

        // Lazy expiry: a still-Open muster past its window flips to Expired (and pays out, for a non-linked muster).
        if (muster.Status == MusterStatus.Open && muster.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
        {
            await CloseAsync(muster.Id, MusterStatus.Expired, ct);
            muster.Status = MusterStatus.Expired;
        }

        if (muster.Status != MusterStatus.Open && !isAdmin)
        {
            return muster.Status == MusterStatus.Expired ? ReactionOutcome.Expired : ReactionOutcome.Closed;
        }

        if (muster.Participants.Any(p => p.UserId == userId))
        {
            return ReactionOutcome.AlreadyParticipated;
        }

        // Capacity is a standalone-muster concept: a linked muster rewards everyone who attended, so its cap is
        // ignored. Staff adds bypass the cap entirely.
        var capped = muster.Capacity is { } cap && muster.SessionLinks.Count == 0 && !isAdmin;
        if (capped && muster.Participants.Count >= muster.Capacity)
        {
            return ReactionOutcome.Full;
        }

        if (!isAdmin && !await auth.IsParticipantAsync(muster.GuildId, userId, ct))
        {
            return ReactionOutcome.NotEligible;
        }

        var participant = new ReactionParticipant
        {
            Id = Guid.NewGuid(),
            MusterId = muster.Id,
            UserId = userId,
            Source = source,
            CheckedInAt = DateTimeOffset.UtcNow,
        };

        try
        {
            // For a capped muster on a real database, serialize the count+insert so concurrent clicks can't oversell
            // the last slot. The in-memory provider (tests) doesn't support transactions, so it falls back to the
            // best-effort count check above + the unique (MusterId,UserId) index.
            if (capped && db.Database.IsRelational())
            {
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var current = await db.ReactionParticipants.CountAsync(p => p.MusterId == muster.Id, ct);
                if (current >= muster.Capacity)
                {
                    return ReactionOutcome.Full;
                }

                db.ReactionParticipants.Add(participant);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            else
            {
                db.ReactionParticipants.Add(participant);
                await db.SaveChangesAsync(ct);
            }
        }
        catch (DbUpdateException)
        {
            // Lost the race on the unique (MusterId,UserId) index — the member is already checked in.
            return ReactionOutcome.AlreadyParticipated;
        }

        return ReactionOutcome.Recorded;
    }

    /// <summary>Remove a member from a muster's roster. Returns false if they weren't on it. No reward reversal is
    /// needed — rewards are only paid at close, so a member removed before close was never paid.</summary>
    public async Task<bool> RemoveParticipantAsync(Guid musterId, ulong userId, CancellationToken ct = default)
    {
        var participant = await db.ReactionParticipants
            .FirstOrDefaultAsync(p => p.MusterId == musterId && p.UserId == userId, ct);
        if (participant is null)
        {
            return false;
        }

        db.ReactionParticipants.Remove(participant);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Set a muster terminal and pay out a non-linked muster's reward to its roster. Idempotent — closing an
    /// already-terminal muster returns false. A <b>linked</b> muster pays at session close (gated on attendance), so
    /// it isn't paid here; a <see cref="MusterStatus.Cancelled"/> muster pays nothing.</summary>
    public async Task<bool> CloseAsync(Guid musterId, MusterStatus status = MusterStatus.Closed, CancellationToken ct = default)
    {
        var muster = await db.FindMusterAsync(musterId, ct);
        if (muster is null || muster.Status != MusterStatus.Open)
        {
            return false;
        }

        muster.Status = status;
        muster.ClosedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // Pay a non-linked muster's reward to everyone on its roster, once, at close. (Linked musters are paid by the
        // session close; cancelled musters pay nothing.) Idempotent per (muster, user) source key.
        if (status != MusterStatus.Cancelled && muster.RewardAmount > 0 && muster.SessionLinks.Count == 0)
        {
            foreach (var p in muster.Participants)
            {
                await awards.AwardAsync(
                    muster.GuildId, p.UserId, muster.CurrencyId, muster.RewardAmount,
                    CurrencyLedgerSource.Muster, $"muster:{muster.Id}:user:{p.UserId}",
                    $"Muster: {muster.Prompt}", ct);
            }
        }

        return true;
    }

    /// <summary>Link a muster to a tracking session (idempotent). Returns false if the link already existed.</summary>
    public async Task<bool> LinkSessionAsync(Guid musterId, Guid sessionId, CancellationToken ct = default)
    {
        if (await db.MusterSessionLinks.AnyAsync(l => l.MusterId == musterId && l.SessionId == sessionId, ct))
        {
            return false;
        }

        db.MusterSessionLinks.Add(new MusterSessionLink { MusterId = musterId, SessionId = sessionId });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Remove a muster↔session link (idempotent). Returns false if there was no such link.</summary>
    public async Task<bool> UnlinkSessionAsync(Guid musterId, Guid sessionId, CancellationToken ct = default)
    {
        var link = await db.MusterSessionLinks
            .FirstOrDefaultAsync(l => l.MusterId == musterId && l.SessionId == sessionId, ct);
        if (link is null)
        {
            return false;
        }

        db.MusterSessionLinks.Remove(link);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
