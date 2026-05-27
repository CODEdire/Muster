using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;

namespace Muster.Infrastructure.Services.Tracking;

/// <summary>
/// Records message activity for monitored text channels (background plane). Scoped to channels with a
/// <see cref="TrackedChannel"/> rule (Mode != Off); untracked channels are ignored. Honors the member's
/// background consent. Stats always; Reward-mode channels also mint POINTS per message
/// (<see cref="TrackedChannel.PointsPerMessage"/>). Dedupes on the message id so gateway redelivery never
/// inflates counts or double-pays.
/// </summary>
public class ActivityService(MusterDbContext db, ICurrencyService awards, GuildAuthorizationService auth)
{
    public async Task RecordMessageAsync(
        ulong guildId, ulong channelId, ulong userId, ulong messageId, DateTimeOffset timestamp,
        CancellationToken ct = default)
    {
        // Scope: only monitored text channels are recorded.
        var channel = await db.FindTrackedChannelAsync(guildId, channelId, ct);
        if (channel is null || channel.Kind != TrackedChannelKind.Text || channel.Mode == TrackedChannelMode.Off)
        {
            return;
        }

        // Consent: message activity is part of the background plane.
        var settings = await db.GetSettingsAsync(guildId, ct);
        var choice = await db.MemberTrackingChoiceAsync(guildId, userId, ct);
        if (!TrackingConsentResolver.Resolve(choice, settings.BackgroundTrackingOptIn).Background)
        {
            return;
        }

        var alreadyRecorded = await db.MessageRecordedAsync(messageId, ct);
        if (alreadyRecorded)
        {
            return;
        }

        db.ActivityRecords.Add(new ActivityRecord
        {
            GuildId = guildId,
            ChannelId = channelId,
            UserId = userId,
            Type = ActivityType.Message,
            Timestamp = timestamp,
            SourceMessageId = messageId,
        });

        var date = DateOnly.FromDateTime(timestamp.UtcDateTime);
        var rollup = await db.FindDailyRollupAsync(guildId, userId, channelId, date, ct);
        if (rollup is null)
        {
            rollup = new DailyActivityRollup
            {
                GuildId = guildId,
                UserId = userId,
                ChannelId = channelId,
                Date = date,
            };
            db.DailyActivityRollups.Add(rollup);
        }

        rollup.MessageCount++;

        // Reward: Reward-mode channels mint POINTS per reward event, gated by messages-per-point, a cooldown,
        // and a per-day cap (any of which may be off). Dedupe above means this runs once per unique message.
        if (channel.Mode == TrackedChannelMode.Reward
            && channel.PointsPerMessage > 0
            && await auth.IsParticipantAsync(guildId, userId, ct))
        {
            await TryRewardMessageAsync(channel, userId, messageId, timestamp, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task TryRewardMessageAsync(
        TrackedChannel channel, ulong userId, ulong messageId, DateTimeOffset now, CancellationToken ct)
    {
        var state = await db.FindMessageRewardStateAsync(channel.GuildId, userId, channel.ChannelId, ct);
        if (state is null)
        {
            state = new MessageRewardState { GuildId = channel.GuildId, UserId = userId, ChannelId = channel.ChannelId };
            db.MessageRewardStates.Add(state);
        }

        state.MessagesSinceAward++;

        // messages-per-point gate
        var perPoint = Math.Max(1, channel.MessagesPerPoint);
        if (state.MessagesSinceAward < perPoint)
        {
            return;
        }

        // cooldown gate (messages keep counting; the reward just waits until the window passes)
        if (channel.MessageCooldownSeconds > 0
            && state.LastRewardAt is { } last
            && now - last < TimeSpan.FromSeconds(channel.MessageCooldownSeconds))
        {
            return;
        }

        // daily-cap gate
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (state.AwardedDate != today)
        {
            state.AwardedDate = today;
            state.AwardedPointsToday = 0;
        }

        var points = channel.PointsPerMessage;
        if (channel.MessageDailyCapPoints > 0)
        {
            var remaining = channel.MessageDailyCapPoints - state.AwardedPointsToday;
            if (remaining <= 0)
            {
                return;
            }

            points = Math.Min(points, remaining);
        }

        state.MessagesSinceAward -= perPoint;
        state.LastRewardAt = now;
        state.AwardedPointsToday += points;

        await awards.StagePointsAsync(
            channel.GuildId, userId, points, CurrencyLedgerSource.Background,
            $"bgmsg:{messageId}", "Message activity", ct);
    }
}
