using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Commands.Tracking;

/// <summary>
/// Platform-independent logic for the per-channel background-tracking config (which channels are
/// monitored and how each is rewarded). Backs the <c>/track-*</c> config commands and, later, the web grid.
/// </summary>
public class TrackedChannelCommandService(MusterDbContext db)
{
    /// <summary>Monitor a voice channel for always-on reward accrual (per-minute, with anti-AFK guards + daily cap).</summary>
    public async Task<CommandResult> SetVoiceAsync(
        ulong guildId, ulong channelId, int pointsPerMinute, int dailyCapPoints,
        bool requireUnmuted, bool requireNotAlone, CancellationToken ct = default)
    {
        if (pointsPerMinute < 0 || dailyCapPoints < 0)
        {
            return CommandResult.Error("Points per minute and the daily cap can't be negative (0 = uncapped).");
        }

        var channel = await Upsert(guildId, channelId, TrackedChannelKind.Voice, ct);
        channel.Mode = TrackedChannelMode.Reward;
        channel.PointsPerMinute = pointsPerMinute;
        channel.DailyCapPoints = dailyCapPoints;
        channel.RequireUnmuted = requireUnmuted;
        channel.RequireNotAlone = requireNotAlone;
        await db.SaveChangesAsync(ct);

        var cap = dailyCapPoints == 0 ? "no daily cap" : $"cap {dailyCapPoints}/day";
        var guards = $"unmuted={(requireUnmuted ? "on" : "off")}, not-alone={(requireNotAlone ? "on" : "off")}";
        return CommandResult.Ok(
            $"Tracking voice <#{channelId}>: {pointsPerMinute} pt/min, {cap}, {guards}. Background pauses while a session runs there.");
    }

    /// <summary>Monitor a text channel for stats (message activity). Rewarding messages is a later phase.</summary>
    public async Task<CommandResult> SetTextAsync(ulong guildId, ulong channelId, CancellationToken ct = default)
    {
        var channel = await Upsert(guildId, channelId, TrackedChannelKind.Text, ct);
        channel.Mode = TrackedChannelMode.StatsOnly;
        await db.SaveChangesAsync(ct);
        return CommandResult.Ok($"Tracking text <#{channelId}> for activity stats.");
    }

    /// <summary>Stop monitoring a channel (removes its rule).</summary>
    public async Task<CommandResult> RemoveAsync(ulong guildId, ulong channelId, CancellationToken ct = default)
    {
        var channel = await db.FindTrackedChannelAsync(guildId, channelId, ct);
        if (channel is null)
        {
            return CommandResult.Error($"<#{channelId}> isn't being tracked.");
        }

        db.TrackedChannels.Remove(channel);
        await db.SaveChangesAsync(ct);
        return CommandResult.Ok($"Stopped tracking <#{channelId}>.");
    }

    public async Task<CommandResult> ListAsync(ulong guildId, CancellationToken ct = default)
    {
        var channels = await db.ListTrackedChannelsAsync(guildId, ct);
        if (channels.Count == 0)
        {
            return CommandResult.Ok("No channels are being tracked. Add one with `/track-voice` or `/track-text`.");
        }

        var lines = channels
            .OrderBy(c => c.Kind).ThenBy(c => c.ChannelId)
            .Select(c => c.Kind == TrackedChannelKind.Voice
                ? $"- 🔊 <#{c.ChannelId}> — {c.Mode}, {c.PointsPerMinute} pt/min" +
                  (c.DailyCapPoints > 0 ? $", cap {c.DailyCapPoints}/day" : string.Empty)
                : $"- 💬 <#{c.ChannelId}> — {c.Mode}");

        return CommandResult.Ok("**Tracked channels**\n" + string.Join('\n', lines));
    }

    private async Task<TrackedChannel> Upsert(ulong guildId, ulong channelId, TrackedChannelKind kind, CancellationToken ct)
    {
        var channel = await db.FindTrackedChannelAsync(guildId, channelId, ct);
        if (channel is null)
        {
            channel = new TrackedChannel { Id = Guid.NewGuid(), GuildId = guildId, ChannelId = channelId };
            db.TrackedChannels.Add(channel);
        }

        channel.Kind = kind;
        return channel;
    }
}
