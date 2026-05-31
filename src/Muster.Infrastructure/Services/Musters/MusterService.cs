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
        ulong guildId, ulong channelId, string? title, string prompt,
        long points, long coins, Guid? coinCurrencyId, int retentionHours,
        int? capacity, DateTimeOffset? expiresAt, ulong createdBy,
        Guid? sessionId = null, bool checkInCreator = false, int? minCheckIns = null, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var muster = new ReactionMuster
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            ChannelId = channelId,
            Title = title,
            Prompt = prompt,
            Points = points,
            Coins = coins,
            CoinCurrencyId = coins > 0 ? coinCurrencyId : null,
            RetentionHours = retentionHours,
            Capacity = capacity,
            MinCheckIns = minCheckIns,
            ExpiresAt = expiresAt,
            Status = MusterStatus.Open,
            CreatedBy = createdBy,
            CreatedAt = now,
        };

        if (sessionId is { } sid)
        {
            muster.SessionLinks.Add(new MusterSessionLink { MusterId = muster.Id, SessionId = sid });
        }

        // Auto-check-in the creator (opt-in). They're added directly here (no eligibility/capacity gate — it's the
        // host opting themselves in at creation), recorded as an Admin-source row.
        if (checkInCreator)
        {
            muster.Participants.Add(new ReactionParticipant
            {
                Id = Guid.NewGuid(),
                MusterId = muster.Id,
                UserId = createdBy,
                Source = MusterParticipantSource.Admin,
                CheckedInAt = now,
            });
        }

        db.ReactionMusters.Add(muster);
        await db.SaveChangesAsync(ct);
        return muster;
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

        // Lazy expiry: a still-Open muster past its window transitions now. A linked muster soft-closes (Locked, pays
        // at session close); a standalone one expires and pays out immediately.
        if (muster.Status == MusterStatus.Open && muster.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
        {
            if (muster.SessionLinks.Count > 0)
            {
                await LockAsync(muster.Id, ct);
                muster.Status = MusterStatus.Locked;
            }
            else
            {
                await CloseAsync(muster.Id, MusterStatus.Expired, ct);
                muster.Status = MusterStatus.Expired;
            }
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
        // Open or Locked (soft-closed) musters can go terminal; an already-terminal one is a no-op.
        if (muster is null || muster.Status is not (MusterStatus.Open or MusterStatus.Locked))
        {
            return false;
        }

        muster.Status = status;
        muster.ClosedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // Pay a non-linked muster's reward (points + coins) to everyone on its roster, once, at close. (Linked musters
        // are paid by the session close; cancelled musters pay nothing.) Idempotent per (muster, user, leg). The
        // minimum-check-ins gate: a roster that fell short of MinCheckIns pays nobody.
        if (status != MusterStatus.Cancelled && muster.SessionLinks.Count == 0
            && (muster.MinCheckIns is not { } min || muster.Participants.Count >= min))
        {
            foreach (var p in muster.Participants)
            {
                await PayAsync(muster, p.UserId, ct);
            }
        }

        return true;
    }

    /// <summary>Award a muster's points + coins to one member, idempotent per leg. Shared by muster-close (non-linked)
    /// and session-close (linked, gated on attendance by the caller).</summary>
    public async Task PayAsync(ReactionMuster muster, ulong userId, CancellationToken ct = default)
    {
        if (muster.Points > 0)
        {
            await awards.AwardPointsAsync(
                muster.GuildId, userId, muster.Points,
                CurrencyLedgerSource.Muster, $"muster:{muster.Id}:user:{userId}:points",
                $"Muster: {muster.Prompt}", ct);
        }

        if (muster.Coins > 0 && muster.CoinCurrencyId is { } coinCcy)
        {
            await awards.AwardAsync(
                muster.GuildId, userId, coinCcy, muster.Coins,
                CurrencyLedgerSource.Muster, $"muster:{muster.Id}:user:{userId}:coins",
                $"Muster: {muster.Prompt}", ct);
        }
    }

    /// <summary>Apply an edit to a live (Open) muster's card + options. The caller has already authorized + validated;
    /// reward values are passed as the final intended values (a creator passes the unchanged template values).
    /// Returns false if the muster is gone or no longer Open.</summary>
    public async Task<bool> EditAsync(
        Guid musterId, string? title, string prompt, int? capacity, DateTimeOffset? expiresAt,
        long points, long coins, Guid? coinCurrencyId, int? minCheckIns, CancellationToken ct = default)
    {
        var muster = await db.FindMusterAsync(musterId, ct);
        if (muster is null || muster.Status != MusterStatus.Open)
        {
            return false;
        }

        muster.Title = title;
        muster.Prompt = prompt;
        muster.Capacity = capacity;
        muster.ExpiresAt = expiresAt;
        muster.Points = points;
        muster.Coins = coins;
        muster.CoinCurrencyId = coins > 0 ? coinCurrencyId : null;
        muster.MinCheckIns = minCheckIns;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Soft-close a muster: stop accepting check-ins without paying or going terminal. Used when a <b>linked</b>
    /// muster hits its max active time — it stays around (Locked) and is paid + closed at session close. Idempotent;
    /// only an Open muster locks.</summary>
    public async Task<bool> LockAsync(Guid musterId, CancellationToken ct = default)
    {
        var muster = await db.FindMusterAsync(musterId, ct);
        if (muster is null || muster.Status != MusterStatus.Open)
        {
            return false;
        }

        muster.Status = MusterStatus.Locked;
        await db.SaveChangesAsync(ct);
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
