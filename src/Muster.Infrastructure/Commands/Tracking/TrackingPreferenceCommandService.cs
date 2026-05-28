using Muster.Domain;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Tracking;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Commands.Tracking;

/// <summary>Platform-independent logic for a member setting a per-guild tracking/privacy preference. Self always;
/// only staff may change another member's preference (gated by <see cref="ITrackingAuthorizer"/> SetPrivacy).</summary>
public class TrackingPreferenceCommandService(MusterDbContext db, ITrackingAuthorizer authorizer)
{
    public async Task<CommandResult> SetAsync(ulong guildId, ulong actorId, ulong userId, TrackingChoice choice, CancellationToken ct = default)
    {
        if (!await authorizer.AuthorizeAsync(new GuildActor(guildId, actorId), userId, TrackingPermission.SetPrivacy, ct))
        {
            return CommandResult.Error("You can only change your own tracking preference.");
        }

        var member = await db.FindMemberAsync(guildId, userId, ct);
        if (member is null)
        {
            member = new GuildMember { GuildId = guildId, UserId = userId, JoinedAt = DateTimeOffset.UtcNow };
            db.GuildMembers.Add(member);
        }

        member.Tracking = choice;
        await db.SaveChangesAsync(ct);

        // Opting out takes effect immediately, even mid-session: evict in-progress tracking rows so the member
        // stops being counted (and stops showing in live ops) right away.
        if (choice is TrackingChoice.AllOut or TrackingChoice.BackgroundOut)
        {
            db.BackgroundVoicePresences.RemoveRange(await db.ListBackgroundPresencesForMemberAsync(guildId, userId, ct));
        }

        if (choice is TrackingChoice.AllOut)
        {
            db.VoiceAttendance.RemoveRange(await db.ListActiveAttendanceForMemberAsync(guildId, userId, ct));
        }

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
