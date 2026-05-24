using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services;

public enum ReactionOutcome
{
    Recorded,
    AlreadyParticipated,
    Expired,
    Full,
    NotTracked,
}

/// <summary>Reaction "muster" check-ins: create the message record and reward members who react.</summary>
public class MusterService(MusterDbContext db, AwardService awards)
{
    public async Task<ReactionMuster> CreateAsync(
        ulong guildId, ulong channelId, ulong messageId, string prompt,
        IEnumerable<string> emojis, Guid currencyId, long rewardAmount,
        int? capacity, DateTimeOffset? expiresAt, CancellationToken ct = default)
    {
        var muster = new ReactionMuster
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            ChannelId = channelId,
            MessageId = messageId,
            Prompt = prompt,
            Emojis = emojis.ToList(),
            CurrencyId = currencyId,
            RewardAmount = rewardAmount,
            Capacity = capacity,
            ExpiresAt = expiresAt,
        };
        db.ReactionMusters.Add(muster);
        await db.SaveChangesAsync(ct);
        return muster;
    }

    /// <summary>Record a reaction on a muster message and reward the member. Idempotent per (muster, user).</summary>
    public async Task<ReactionOutcome> RecordReactionAsync(
        ulong messageId, ulong userId, string emoji, CancellationToken ct = default)
    {
        var muster = await db.ReactionMusters
            .Include(m => m.Participants)
            .FirstOrDefaultAsync(m => m.MessageId == messageId, ct);

        if (muster is null || (muster.Emojis.Count > 0 && !muster.Emojis.Contains(emoji)))
        {
            return ReactionOutcome.NotTracked;
        }

        if (muster.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
        {
            return ReactionOutcome.Expired;
        }

        if (muster.Participants.Any(p => p.UserId == userId))
        {
            return ReactionOutcome.AlreadyParticipated;
        }

        if (muster.Capacity is { } cap && muster.Participants.Count >= cap)
        {
            return ReactionOutcome.Full;
        }

        db.ReactionParticipants.Add(new ReactionParticipant
        {
            Id = Guid.NewGuid(),
            MusterId = muster.Id,
            UserId = userId,
            Emoji = emoji,
            ReactedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        await awards.AwardAsync(
            muster.GuildId, userId, muster.CurrencyId, muster.RewardAmount,
            LedgerSourceType.Muster, $"muster:{muster.Id}:user:{userId}",
            $"Reaction muster: {muster.Prompt}", ct);

        return ReactionOutcome.Recorded;
    }
}
