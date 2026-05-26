using Muster.Domain.Enums;

namespace Muster.Domain.Entities.Events;

/// <summary>
/// A scheduled operation members sign up for and are awarded on attendance when it closes. A lighter
/// state machine than <see cref="GuildQuest"/> — no claim/submit/review flow, just sign-up and attendance:
/// <c>Scheduled → Open → Closed</c> (or <c>Cancelled</c>). May optionally bind a voice tracking session.
/// </summary>
public class GuildEvent
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public EventStatus Status { get; set; } = EventStatus.Scheduled;
    public DateTimeOffset StatusChangedAt { get; set; }

    public ulong CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Flat attendance reward paid to each attendee on close.</summary>
    public Guid RewardCurrencyId { get; set; }
    public long RewardAmount { get; set; }

    public DateTimeOffset? ScheduledStart { get; set; }
    public DateTimeOffset? ScheduledEnd { get; set; }

    /// <summary>The channel/message the event is announced in (for RSVP edits).</summary>
    public ulong? ChannelId { get; set; }
    public ulong? MessageId { get; set; }

    /// <summary>Optional voice tracking session bound to this event.</summary>
    public Guid? TrackingSessionId { get; set; }

    public List<EventAttendee> Attendees { get; set; } = [];

    /// <summary>Build a new event, Open now or Scheduled when the start is in the future.</summary>
    public static GuildEvent Create(
        ulong guildId, ulong createdBy, string name, string description, Guid rewardCurrencyId, long rewardAmount,
        DateTimeOffset? scheduledStart = null, DateTimeOffset? scheduledEnd = null)
    {
        var now = DateTimeOffset.UtcNow;
        var scheduled = scheduledStart is { } s && s > now;

        return new GuildEvent
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            CreatedBy = createdBy,
            Name = name.Trim(),
            Description = (description ?? string.Empty).Trim(),
            Status = scheduled ? EventStatus.Scheduled : EventStatus.Open,
            StatusChangedAt = now,
            CreatedAt = now,
            RewardCurrencyId = rewardCurrencyId,
            RewardAmount = rewardAmount,
            ScheduledStart = scheduledStart,
            ScheduledEnd = scheduledEnd,
        };
    }

    /// <summary>Move to a new status, keeping <see cref="StatusChangedAt"/> in lock-step. Throws if terminal.</summary>
    public void TransitionTo(EventStatus next)
    {
        if (Status == next)
        {
            return;
        }

        if (Status is EventStatus.Closed or EventStatus.Cancelled)
        {
            throw new InvalidOperationException($"Event {Id} is in terminal state {Status}; cannot transition to {next}.");
        }

        Status = next;
        StatusChangedAt = DateTimeOffset.UtcNow;
    }

    public void Activate() => TransitionTo(EventStatus.Open);
    public void Close() => TransitionTo(EventStatus.Closed);
    public void Cancel() => TransitionTo(EventStatus.Cancelled);
}

public class EventAttendee
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public ulong UserId { get; set; }

    public AttendanceStatus Status { get; set; }
    public DateTimeOffset SignedUpAt { get; set; }

    /// <summary>When the attendee was marked attended / no-show on close.</summary>
    public DateTimeOffset? MarkedAt { get; set; }

    public GuildEvent? Event { get; set; }

    public static EventAttendee NewSignUp(Guid eventId, ulong userId) => new()
    {
        Id = Guid.NewGuid(),
        EventId = eventId,
        UserId = userId,
        Status = AttendanceStatus.SignedUp,
        SignedUpAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Mark attended on close. Idempotent; valid from SignedUp.</summary>
    public void MarkAttended()
    {
        if (Status is not (AttendanceStatus.SignedUp or AttendanceStatus.Attended))
        {
            throw new InvalidOperationException($"Attendee {UserId} cannot attend from {Status}.");
        }

        Status = AttendanceStatus.Attended;
        MarkedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Mark no-show on close. Valid from SignedUp.</summary>
    public void MarkNoShow()
    {
        Status = AttendanceStatus.NoShow;
        MarkedAt = DateTimeOffset.UtcNow;
    }
}
