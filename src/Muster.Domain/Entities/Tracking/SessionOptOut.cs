namespace Muster.Domain.Entities.Tracking;

/// <summary>
/// A member's one-time opt-out from a single tracking session. Unlike the standing <c>TrackingChoice</c>
/// preference, this excludes the member from just this session (for its remainder): the reconcile skips them
/// and their attendance row is removed when they opt out.
/// </summary>
public class SessionOptOut
{
    public long Id { get; set; }
    public Guid SessionId { get; set; }
    public ulong UserId { get; set; }
}
