using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Tracking;
using Xunit;

namespace Muster.UnitTests;

public class SessionTimelineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 27, 19, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = T0.AddMinutes(60);

    private static DateTimeOffset At(int min) => T0.AddMinutes(min);
    private static PresencePoint Ev(ulong u, SessionPresenceKind k, int min) => new(u, k, At(min));
    private static HashSet<ulong> Members(params ulong[] ids) => [.. ids];

    [Fact]
    public void NoEvents_IsEmpty()
    {
        var model = SessionTimeline.Build([], T0, End, Members(1));
        Assert.Empty(model.Tracks);
        Assert.Empty(model.Occupancy);
        Assert.Equal(0, model.Peak);
    }

    [Fact]
    public void JoinResumePauseLeave_ProducesEarningThenPausedSegments()
    {
        PresencePoint[] events =
        [
            Ev(1, SessionPresenceKind.Join, 0),
            Ev(1, SessionPresenceKind.Resume, 0),  // earning from 0
            Ev(1, SessionPresenceKind.Pause, 10),  // paused 10..
            Ev(1, SessionPresenceKind.Leave, 15),  // gone at 15
        ];

        var track = Assert.Single(SessionTimeline.Build(events, T0, End, Members(1)).Tracks);
        Assert.Equal(At(0), track.Start);
        Assert.Equal(At(15), track.End);

        Assert.Collection(track.Segments,
            s => { Assert.True(s.Earning); Assert.Equal(At(0), s.Start); Assert.Equal(At(10), s.End); },
            s => { Assert.False(s.Earning); Assert.Equal(At(10), s.Start); Assert.Equal(At(15), s.End); });
    }

    [Fact]
    public void OpenSegment_ClampsToWindowEnd()
    {
        // Joined + earning, never left: the open run extends to the window end.
        PresencePoint[] events = [Ev(1, SessionPresenceKind.Join, 5), Ev(1, SessionPresenceKind.Resume, 5)];

        var track = Assert.Single(SessionTimeline.Build(events, T0, End, Members(1)).Tracks);
        var seg = Assert.Single(track.Segments);
        Assert.True(seg.Earning);
        Assert.Equal(At(5), seg.Start);
        Assert.Equal(End, seg.End);
    }

    [Fact]
    public void RejoinGap_LeavesABreakBetweenRuns()
    {
        PresencePoint[] events =
        [
            Ev(1, SessionPresenceKind.Join, 0),
            Ev(1, SessionPresenceKind.Leave, 10), // gap 10..20
            Ev(1, SessionPresenceKind.Join, 20),
            Ev(1, SessionPresenceKind.Leave, 30),
        ];

        var track = Assert.Single(SessionTimeline.Build(events, T0, End, Members(1)).Tracks);
        // Two non-adjacent presence segments: 0..10 and 20..30 (the gap is not bridged).
        Assert.Collection(track.Segments,
            s => { Assert.Equal(At(0), s.Start); Assert.Equal(At(10), s.End); },
            s => { Assert.Equal(At(20), s.Start); Assert.Equal(At(30), s.End); });
    }

    [Fact]
    public void Occupancy_CountsOverlapAndPeaks()
    {
        PresencePoint[] events =
        [
            Ev(1, SessionPresenceKind.Join, 0),
            Ev(2, SessionPresenceKind.Join, 5),   // 2 present 5..10
            Ev(1, SessionPresenceKind.Leave, 10),
            Ev(2, SessionPresenceKind.Leave, 20),
        ];

        var model = SessionTimeline.Build(events, T0, End, Members(1, 2));
        Assert.Equal(2, model.Peak);

        // Headcount after each change-point: 1 @0, 2 @5, 1 @10, 0 @20.
        Assert.Collection(model.Occupancy,
            s => { Assert.Equal(At(0), s.At); Assert.Equal(1, s.Count); },
            s => { Assert.Equal(At(5), s.At); Assert.Equal(2, s.Count); },
            s => { Assert.Equal(At(10), s.At); Assert.Equal(1, s.Count); },
            s => { Assert.Equal(At(20), s.At); Assert.Equal(0, s.Count); });
    }

    [Fact]
    public void PausedMemberStillCountsAsPresent()
    {
        // Member is present-but-paused the whole time → occupancy still counts them.
        PresencePoint[] events = [Ev(1, SessionPresenceKind.Join, 0), Ev(1, SessionPresenceKind.Leave, 30)];

        var model = SessionTimeline.Build(events, T0, End, Members(1));
        Assert.Equal(1, model.Peak);
        var seg = Assert.Single(Assert.Single(model.Tracks).Segments);
        Assert.False(seg.Earning); // never resumed → paused presence
    }

    [Fact]
    public void EventsForNonMembers_AreIgnored()
    {
        // A dropped drive-by attendee (no attendance row) leaves orphan events; the projection skips them.
        PresencePoint[] events = [Ev(99, SessionPresenceKind.Join, 0), Ev(99, SessionPresenceKind.Leave, 1)];

        var model = SessionTimeline.Build(events, T0, End, Members(1));
        Assert.Empty(model.Tracks);
        Assert.Equal(0, model.Peak);
    }
}
