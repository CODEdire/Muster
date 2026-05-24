using Muster.Domain.Entities;
using Muster.Infrastructure.Services;

namespace Muster.Infrastructure.Commands;

/// <summary>Platform-independent logic for event-op commands (scheduled missions with sign-up).</summary>
public class OpCommandService(MissionService missions)
{
    public async Task<CommandResult> CreateAsync(
        ulong guildId, ulong actorId, string name, string description, long reward, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return CommandResult.Error("Please provide an op name.");
        }

        if (reward < 0)
        {
            return CommandResult.Error("Reward can't be negative.");
        }

        var op = await missions.CreateEventOpPointsAsync(guildId, name.Trim(), (description ?? string.Empty).Trim(), actorId, reward, ct: ct);
        return CommandResult.Ok($"Event op **{op.Name}** created (`{op.Id}`). Members can `/op-signup`.");
    }

    public async Task<CommandResult> ListAsync(ulong guildId, CancellationToken ct = default)
    {
        var ops = await missions.ListOpenEventOpsAsync(guildId, ct);
        return CommandResult.Ok(FormatOps(ops));
    }

    public async Task<CommandResult> SignUpAsync(ulong guildId, string opIdRaw, ulong userId, CancellationToken ct = default)
        => await GuardedAsync(opIdRaw, async id =>
        {
            await missions.SignUpAsync(id, userId, ct);
            return CommandResult.Ok("You're signed up. You'll be awarded when the op is closed.");
        });

    public async Task<CommandResult> CloseAsync(ulong guildId, string opIdRaw, CancellationToken ct = default)
        => await GuardedAsync(opIdRaw, async id =>
        {
            var awarded = await missions.CloseEventOpAsync(id, ct);
            return CommandResult.Ok($"Closed the op and awarded **{awarded}** attendee(s).");
        });

    public static string FormatOps(IReadOnlyList<Mission> ops)
    {
        if (ops.Count == 0)
        {
            return "No open event ops right now.";
        }

        var lines = ops.Select(o => $"`{o.Id}` — **{o.Name}**: {o.Description} (reward {o.RewardAmount})");
        return "**Open event ops**\n" + string.Join("\n", lines);
    }

    private static async Task<CommandResult> GuardedAsync(string idRaw, Func<Guid, Task<CommandResult>> action)
    {
        if (!Guid.TryParse(idRaw, out var id))
        {
            return CommandResult.Error("That doesn't look like a valid op id.");
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
