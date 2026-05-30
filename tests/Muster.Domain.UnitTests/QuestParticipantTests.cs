using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Xunit;

namespace Muster.UnitTests;

/// <summary>Participant state transitions that the cards/notes depend on.</summary>
public class QuestParticipantTests
{
    [Fact]
    public void Resubmit_ClearsPriorReviewerFeedback()
    {
        var p = new QuestParticipant
        {
            UserId = 1,
            Status = QuestParticipantStatus.RevisionRequested,
            Note = "first attempt",
            ReviewNote = "needs more detail",
            ReviewedBy = 99,
            ReviewedAt = DateTimeOffset.UnixEpoch,
        };

        p.Submit("fixed it");

        Assert.Equal(QuestParticipantStatus.Submitted, p.Status);
        Assert.Equal("fixed it", p.Note);          // the new note shows
        Assert.Null(p.ReviewNote);                  // stale feedback gone (no longer shadows the note on the card)
        Assert.Null(p.ReviewedBy);
        Assert.Null(p.ReviewedAt);
    }
}
