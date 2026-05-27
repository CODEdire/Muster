using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Entities;
using Muster.Domain.Entities.Members;
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
        bool requireUnmuted, bool requireUndeafened, bool requireNotAlone, string? channelName = null, CancellationToken ct = default)
    {
        if (pointsPerMinute < 0 || dailyCapPoints < 0)
        {
            return CommandResult.Error("Points per minute and the daily cap can't be negative (0 = uncapped).");
        }

        var channel = await Upsert(guildId, channelId, GuildChannelKind.Voice, channelName, ct);
        channel.Mode = TrackedChannelMode.Reward;
        channel.PointsPerMinute = pointsPerMinute;
        channel.DailyCapPoints = dailyCapPoints;
        channel.Guards = AfkGuardsExtensions.Compose(requireUnmuted, requireUndeafened, requireNotAlone);
        await db.SaveChangesAsync(ct);

        var cap = dailyCapPoints == 0 ? "no daily cap" : $"cap {dailyCapPoints}/day";
        var guards = $"unmuted={(requireUnmuted ? "on" : "off")}, undeafened={(requireUndeafened ? "on" : "off")}, not-alone={(requireNotAlone ? "on" : "off")}";
        return CommandResult.Ok(
            $"Tracking voice <#{channelId}>: {pointsPerMinute} pt/min, {cap}, {guards}. Background pauses while a session runs there.");
    }

    /// <summary>Monitor a text channel. With <paramref name="pointsPerMessage"/> &gt; 0 it rewards (gated by
    /// messages-per-point, a cooldown, and a daily cap); otherwise it's stats-only.</summary>
    public async Task<CommandResult> SetTextAsync(
        ulong guildId, ulong channelId, int pointsPerMessage = 0, int messagesPerPoint = 1,
        int cooldownSeconds = 0, int dailyCapPoints = 0, string? channelName = null, CancellationToken ct = default)
    {
        if (pointsPerMessage < 0 || messagesPerPoint < 0 || cooldownSeconds < 0 || dailyCapPoints < 0)
        {
            return CommandResult.Error("Message-reward values can't be negative.");
        }

        var channel = await Upsert(guildId, channelId, GuildChannelKind.Text, channelName, ct);
        channel.Mode = pointsPerMessage > 0 ? TrackedChannelMode.Reward : TrackedChannelMode.StatsOnly;
        channel.PointsPerMessage = pointsPerMessage;
        channel.MessagesPerPoint = Math.Max(1, messagesPerPoint);
        channel.MessageCooldownSeconds = cooldownSeconds;
        channel.MessageDailyCapPoints = dailyCapPoints;
        await db.SaveChangesAsync(ct);

        if (pointsPerMessage == 0)
        {
            return CommandResult.Ok($"Tracking text <#{channelId}> for activity stats.");
        }

        var perPoint = Math.Max(1, messagesPerPoint);
        var cap = dailyCapPoints == 0 ? "no daily cap" : $"cap {dailyCapPoints}/day";
        var cd = cooldownSeconds == 0 ? "no cooldown" : $"{cooldownSeconds}s cooldown";
        return CommandResult.Ok(
            $"Rewarding text <#{channelId}>: {pointsPerMessage} pt per {perPoint} msg(s), {cd}, {cap}.");
    }

    /// <summary>Stop monitoring a channel. A live channel keeps its roster row (Mode → Off); a channel whose
    /// Discord channel is already gone (soft-deleted) is removed outright so the stale config is cleaned up.</summary>
    public async Task<CommandResult> RemoveAsync(ulong guildId, ulong channelId, CancellationToken ct = default)
    {
        var channel = await db.FindChannelAsync(guildId, channelId, ct);
        if (channel is null || channel.Mode == TrackedChannelMode.Off)
        {
            return CommandResult.Error($"<#{channelId}> isn't being tracked.");
        }

        if (channel.DeletedAt is not null)
        {
            db.GuildChannels.Remove(channel); // gone from Discord — clear the leftover config
        }
        else
        {
            channel.Mode = TrackedChannelMode.Off; // keep the roster entry, just stop monitoring
        }

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
            .Select(c => (c.Kind == GuildChannelKind.Voice
                ? $"- 🔊 <#{c.ChannelId}> — {c.Mode}, {c.PointsPerMinute} pt/min" +
                  (c.DailyCapPoints > 0 ? $", cap {c.DailyCapPoints}/day" : string.Empty)
                : $"- 💬 <#{c.ChannelId}> — {c.Mode}")
                + (c.DeletedAt is not null ? " *(channel deleted)*" : string.Empty));

        return CommandResult.Ok("**Tracked channels**\n" + string.Join('\n', lines));
    }

    private async Task<GuildChannel> Upsert(ulong guildId, ulong channelId, GuildChannelKind kind, string? channelName, CancellationToken ct)
    {
        var channel = await db.FindChannelAsync(guildId, channelId, ct);
        if (channel is null)
        {
            // Tracking a channel the roster sync hasn't captured yet — create a config-bearing row; a later
            // sync fills in the synced name/position without disturbing this config.
            channel = new GuildChannel { GuildId = guildId, ChannelId = channelId };
            db.GuildChannels.Add(channel);
        }

        channel.Kind = kind;
        channel.DeletedAt = null; // (re)tracking implies the channel is in use
        if (!string.IsNullOrWhiteSpace(channelName) && string.IsNullOrWhiteSpace(channel.Name))
        {
            channel.Name = channelName; // only fill a missing name; sync owns the authoritative name
        }

        return channel;
    }
}
