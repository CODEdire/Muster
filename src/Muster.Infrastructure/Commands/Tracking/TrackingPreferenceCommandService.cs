using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Commands.Tracking;

/// <summary>Platform-independent logic for a member setting their own per-guild tracking/privacy preference.</summary>
public class TrackingPreferenceCommandService(MusterDbContext db)
{
    public async Task<CommandResult> SetAsync(ulong guildId, ulong userId, TrackingChoice choice, CancellationToken ct = default)
    {
        var member = await db.FindMemberAsync(guildId, userId, ct);
        if (member is null)
        {
            member = new GuildMember { GuildId = guildId, UserId = userId, JoinedAt = DateTimeOffset.UtcNow };
            db.GuildMembers.Add(member);
        }

        member.Tracking = choice;
        await db.SaveChangesAsync(ct);

        return CommandResult.Ok(choice switch
        {
            TrackingChoice.In => "You're opted **in** to all participation tracking on this server.",
            TrackingChoice.BackgroundOut => "You've opted **out of background tracking** (passive voice/message stats & rewards). You're still tracked in sessions/events you join.",
            TrackingChoice.AllOut => "You've opted **out of all tracking** on this server — background and sessions.",
            _ => "Your tracking preference follows the server default. Background tracking applies unless this server is opt-in.",
        });
    }
}
