using Muster.Domain.Entities;
using Muster.Infrastructure.Services.Events;

namespace Muster.Infrastructure.Commands.Events;

/// <summary>Platform-independent logic for event commands (scheduled ops with sign-up).</summary>
public class OpCommandService(GuildEventService events)
{
    public async Task<CommandResult> CreateAsync(
        ulong guildId, ulong actorId, string name, string description, long reward, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return CommandResult.Error("Please provide an event name.");
        }

        if (reward < 0)
        {
            return CommandResult.Error("Reward can't be negative.");
        }

        var ev = await events.CreateEventPointsAsync(guildId, name.Trim(), (description ?? string.Empty).Trim(), actorId, reward, ct: ct);
        return CommandResult.Ok($"Event **{ev.Name}** created (`{ev.Id}`). Members can `/op-signup`.");
    }

    public async Task<CommandResult> ListAsync(ulong guildId, CancellationToken ct = default)
    {
        var open = await events.ListOpenEventsAsync(guildId, ct);
        return CommandResult.Ok(FormatOps(open));
    }

    public async Task<CommandResult> SignUpAsync(ulong guildId, string opIdRaw, ulong userId, CancellationToken ct = default)
        => await GuardedAsync(opIdRaw, async id =>
        {
            await events.SignUpAsync(id, userId, ct);
            return CommandResult.Ok("You're signed up. You'll be awarded when the event is closed.");
        });

    public async Task<CommandResult> CloseAsync(ulong guildId, string opIdRaw, CancellationToken ct = default)
        => await GuardedAsync(opIdRaw, async id =>
        {
            var awarded = await events.CloseEventAsync(id, ct);
            return CommandResult.Ok($"Closed the event and awarded **{awarded}** attendee(s).");
        });

    public static string FormatOps(IReadOnlyList<GuildEvent> events)
    {
        if (events.Count == 0)
        {
            return "No open events right now.";
        }

        var lines = events.Select(o => $"`{o.Id}` — **{o.Name}**: {o.Description} (reward {o.RewardAmount})");
        return "**Open events**\n" + string.Join("\n", lines);
    }

    private static async Task<CommandResult> GuardedAsync(string idRaw, Func<Guid, Task<CommandResult>> action)
    {
        if (!Guid.TryParse(idRaw, out var id))
        {
            return CommandResult.Error("That doesn't look like a valid event id.");
        }

        try
        {
            return await action(id);
        }
        catch (InvalidOperationException ex)
        {
            return CommandResult.Error(ex.Message);
        }
    }
}
