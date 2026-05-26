using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Ledger;
using Muster.Infrastructure.Services.Membership;

namespace Muster.Infrastructure.Services.Events;

/// <summary>
/// Guild events: scheduled ops members sign up for and are awarded on attendance when the event closes.
/// A distinct, lighter mechanic from the claim → submit → settle quest lifecycle — sign-up and attendance
/// only. Awards go through <see cref="ICurrencyService"/> and are idempotent per (event, user).
/// </summary>
public class GuildEventService(
    MusterDbContext db,
    ICurrencyService awards,
    GuildAuthorizationService auth)
{
    public async Task<GuildEvent> CreateEventPointsAsync(
        ulong guildId, string name, string description, ulong createdBy, long rewardPoints,
        DateTimeOffset? scheduledStart = null, DateTimeOffset? scheduledEnd = null, CancellationToken ct = default)
    {
        var points = await db.FindPointsAsync(guildId, ct)
            ?? throw new InvalidOperationException($"POINTS currency not provisioned for guild {guildId}.");

        var ev = GuildEvent.Create(guildId, createdBy, name, description, points.Id, rewardPoints, scheduledStart, scheduledEnd);
        db.GuildEvents.Add(ev);
        await db.SaveChangesAsync(ct);
        return ev;
    }

    public async Task<IReadOnlyList<GuildEvent>> ListOpenEventsAsync(ulong guildId, CancellationToken ct = default)
        => await db.ListOpenEventsAsync(guildId, ct);

    public async Task SignUpAsync(Guid eventId, ulong userId, CancellationToken ct = default)
    {
        var ev = await db.FindEventAsync(eventId, ct)
            ?? throw new InvalidOperationException("Event not found.");

        if (!await auth.IsParticipantAsync(ev.GuildId, userId, ct))
        {
            throw new InvalidOperationException("You're not eligible to participate in this server.");
        }

        if (ev.Attendees.Any(a => a.UserId == userId))
        {
            return;
        }

        db.EventAttendees.Add(EventAttendee.NewSignUp(ev.Id, userId));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Close an event: mark signed-up members attended and award them. Returns members awarded.</summary>
    public async Task<int> CloseEventAsync(Guid eventId, CancellationToken ct = default)
    {
        var ev = await db.FindEventAsync(eventId, ct)
            ?? throw new InvalidOperationException("Event not found.");

        ev.Close();

        var attendees = ev.Attendees
            .Where(a => a.Status is AttendanceStatus.SignedUp or AttendanceStatus.Attended)
            .ToList();

        foreach (var attendee in attendees)
        {
            attendee.MarkAttended();
        }

        await db.SaveChangesAsync(ct);

        foreach (var attendee in attendees)
        {
            await awards.AwardAsync(
                ev.GuildId, attendee.UserId, ev.RewardCurrencyId, ev.RewardAmount,
                LedgerSourceType.Event, $"event:{eventId}:user:{attendee.UserId}",
                $"Event attendance: {ev.Name}", ct);
        }

        return attendees.Count;
    }
}
