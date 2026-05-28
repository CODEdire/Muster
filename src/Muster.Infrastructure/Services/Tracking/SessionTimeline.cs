using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services.Tracking;

/// <summary>One presence transition, reduced to what the timeline projection needs.</summary>
public readonly record struct PresencePoint(ulong UserId, SessionPresenceKind Kind, DateTimeOffset At);

/// <summary>A run within a member's presence: earning (eligible) or paused (present but a guard tripped).</summary>
public record TimelineSegment(DateTimeOffset Start, DateTimeOffset End, bool Earning);

/// <summary>One member's attendance timeline: overall presence extent + the earning/paused runs inside it
/// (with rejoin gaps showing as breaks between non-adjacent segments).</summary>
public record MemberTrack(ulong UserId, DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<TimelineSegment> Segments);

/// <summary>Headcount in the channel as of <see cref="At"/> — a step in the occupancy curve.</summary>
public record OccupancyStep(DateTimeOffset At, int Count);

/// <summary>The projected timeline for a session: per-member tracks + the occupancy step series + peak headcount.</summary>
public record SessionTimelineModel(IReadOnlyList<MemberTrack> Tracks, IReadOnlyList<OccupancyStep> Occupancy, int Peak)
{
    public static readonly SessionTimelineModel Empty = new([], [], 0);
}

/// <summary>
/// Folds a session's presence event stream into exact attendance segments + an occupancy step curve. Pure and
/// deterministic (no DB / clock): open runs are clamped to <c>winEnd</c> (the live "now" or the session's end).
/// Only members in <c>memberIds</c> are projected, so events from a dropped drive-by attendee are ignored.
/// </summary>
public static class SessionTimeline
{
    public static SessionTimelineModel Build(
        IEnumerable<PresencePoint> events, DateTimeOffset winStart, DateTimeOffset winEnd, ISet<ulong> memberIds)
    {
        var tracks = new List<MemberTrack>();
        var runs = new List<(DateTimeOffset Start, DateTimeOffset End)>(); // merged presence runs across all members

        foreach (var group in events.Where(e => memberIds.Contains(e.UserId)).GroupBy(e => e.UserId))
        {
            var segs = FoldMember(group.OrderBy(e => e.At), winStart, winEnd);
            if (segs.Count == 0)
            {
                continue;
            }

            tracks.Add(new MemberTrack(group.Key, segs[0].Start, segs[^1].End, segs));
            runs.AddRange(MergeRuns(segs));
        }

        tracks.Sort((a, b) => a.Start.CompareTo(b.Start));
        var (occupancy, peak) = Sweep(runs);
        return new SessionTimelineModel(tracks, occupancy, peak);
    }

    /// <summary>Replay one member's ordered events into earning/paused segments, clamped to the window.</summary>
    private static List<TimelineSegment> FoldMember(IEnumerable<PresencePoint> ordered, DateTimeOffset winStart, DateTimeOffset winEnd)
    {
        var segs = new List<TimelineSegment>();
        var inChannel = false;
        var earning = false;
        var segStart = winStart;

        void Emit(DateTimeOffset end) { if (end > segStart) { segs.Add(new TimelineSegment(segStart, end, earning)); } }

        foreach (var e in ordered)
        {
            var at = Clamp(e.At, winStart, winEnd);
            switch (e.Kind)
            {
                case SessionPresenceKind.Join:
                    if (!inChannel) { inChannel = true; earning = false; segStart = at; }
                    break;
                case SessionPresenceKind.Resume:
                    if (!inChannel) { inChannel = true; earning = true; segStart = at; } // defensive: resume w/o join
                    else if (!earning) { Emit(at); earning = true; segStart = at; }
                    break;
                case SessionPresenceKind.Pause:
                    if (inChannel && earning) { Emit(at); earning = false; segStart = at; }
                    break;
                case SessionPresenceKind.Leave:
                    if (inChannel) { Emit(at); inChannel = false; }
                    break;
            }
        }

        if (inChannel) { Emit(winEnd); } // still present at window end → clamp the open run
        return segs;
    }

    /// <summary>Coalesce a member's adjacent segments (sharing a boundary) into presence runs; non-adjacent
    /// segments are rejoin gaps and stay separate.</summary>
    private static List<(DateTimeOffset Start, DateTimeOffset End)> MergeRuns(List<TimelineSegment> segs)
    {
        var runs = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        var start = segs[0].Start;
        var end = segs[0].End;
        for (var i = 1; i < segs.Count; i++)
        {
            if (segs[i].Start == end) { end = segs[i].End; }
            else { runs.Add((start, end)); start = segs[i].Start; end = segs[i].End; }
        }
        runs.Add((start, end));
        return runs;
    }

    /// <summary>Edge-sweep presence runs into a step series of headcount-over-time, plus the peak.</summary>
    private static (List<OccupancyStep> Steps, int Peak) Sweep(List<(DateTimeOffset Start, DateTimeOffset End)> runs)
    {
        if (runs.Count == 0)
        {
            return ([], 0);
        }

        // +1 at a run start, -1 at its end; at equal timestamps apply joins before leaves to avoid a phantom dip.
        var deltas = new List<(DateTimeOffset At, int D)>(runs.Count * 2);
        foreach (var (s, e) in runs) { deltas.Add((s, +1)); deltas.Add((e, -1)); }
        deltas.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : b.D.CompareTo(a.D));

        var steps = new List<OccupancyStep>();
        var count = 0;
        var peak = 0;
        var i = 0;
        while (i < deltas.Count)
        {
            var at = deltas[i].At;
            while (i < deltas.Count && deltas[i].At == at) { count += deltas[i].D; i++; }
            steps.Add(new OccupancyStep(at, count));
            if (count > peak) { peak = count; }
        }

        return (steps, peak);
    }

    private static DateTimeOffset Clamp(DateTimeOffset v, DateTimeOffset lo, DateTimeOffset hi)
        => v < lo ? lo : v > hi ? hi : v;
}
