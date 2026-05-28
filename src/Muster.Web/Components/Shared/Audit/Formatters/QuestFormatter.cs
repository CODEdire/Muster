using System.Text.Json;
using Muster.Infrastructure.Services.Platform;

namespace Muster.Web.Components.Shared.Audit.Formatters;

/// <summary>
/// Renders quest lifecycle audit rows — post, edit, cancel, claim, submit, the review verdicts, and the
/// intake/final/dispute outcomes. Quest ids render as a CodeToken (8-char prefix) since we don't yet have
/// a quest-name lookup batcher; member ids resolve via the user lookup like every other UserToken.
/// </summary>
public class QuestFormatter : IAuditFormatter
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> _keys =
    [
        AuditActions.Quests.Post.Key, AuditActions.Quests.Edit.Key, AuditActions.Quests.Cancel.Key,
        AuditActions.Quests.Claim.Key, AuditActions.Quests.Submit.Key,
        AuditActions.Quests.Approve.Key, AuditActions.Quests.Reject.Key, AuditActions.Quests.ReopenRejection.Key,
        AuditActions.Quests.Confirm.Key, AuditActions.Quests.Dispute.Key, AuditActions.Quests.Arbitrate.Key,
        AuditActions.Quests.RequestRevision.Key,
        AuditActions.Quests.AcceptIntake.Key, AuditActions.Quests.RejectIntake.Key,
        AuditActions.Quests.Finalize.Key, AuditActions.Quests.ReleaseClaim.Key,
    ];

    public bool Matches(string action) => _keys.Contains(action);

    public AuditRender Render(AuditEntryView entry)
    {
        if (string.IsNullOrEmpty(entry.Details)) return Empty(entry);

        try
        {
            return entry.Action switch
            {
                var k when k == AuditActions.Quests.Post.Key
                    => RenderPost(entry, JsonSerializer.Deserialize<QuestPostPayload>(entry.Details, _json)),
                var k when k == AuditActions.Quests.Edit.Key
                    => RenderEdit(entry, JsonSerializer.Deserialize<QuestEditPayload>(entry.Details, _json)),
                var k when k == AuditActions.Quests.Cancel.Key
                    => RenderQuestId(entry, "Cancelled quest", JsonSerializer.Deserialize<QuestCancelPayload>(entry.Details, _json)?.QuestId),
                var k when k == AuditActions.Quests.Claim.Key
                    => RenderClaim(entry, JsonSerializer.Deserialize<QuestClaimPayload>(entry.Details, _json)),
                var k when k == AuditActions.Quests.Submit.Key
                    => RenderSubmit(entry, JsonSerializer.Deserialize<QuestSubmitPayload>(entry.Details, _json)),
                var k when k == AuditActions.Quests.Approve.Key
                    => RenderReview(entry, "Approved submission from", JsonSerializer.Deserialize<QuestReviewPayload>(entry.Details, _json)),
                var k when k == AuditActions.Quests.Reject.Key
                    => RenderReview(entry, "Rejected submission from", JsonSerializer.Deserialize<QuestReviewPayload>(entry.Details, _json)),
                var k when k == AuditActions.Quests.ReopenRejection.Key
                    => RenderReview(entry, "Reopened rejection for", JsonSerializer.Deserialize<QuestReviewPayload>(entry.Details, _json)),
                var k when k == AuditActions.Quests.Confirm.Key
                    => RenderQuestId(entry, "Confirmed quest", JsonSerializer.Deserialize<QuestConfirmPayload>(entry.Details, _json)?.QuestId),
                var k when k == AuditActions.Quests.Dispute.Key
                    => RenderDispute(entry, JsonSerializer.Deserialize<QuestDisputePayload>(entry.Details, _json)),
                var k when k == AuditActions.Quests.Arbitrate.Key
                    => RenderArbitrate(entry, JsonSerializer.Deserialize<QuestArbitratePayload>(entry.Details, _json)),
                var k when k == AuditActions.Quests.RequestRevision.Key
                    => RenderRevision(entry, JsonSerializer.Deserialize<QuestRevisionPayload>(entry.Details, _json)),
                var k when k == AuditActions.Quests.AcceptIntake.Key
                    => RenderAcceptIntake(entry, JsonSerializer.Deserialize<QuestAcceptIntakePayload>(entry.Details, _json)),
                var k when k == AuditActions.Quests.RejectIntake.Key
                    => RenderQuestId(entry, "Rejected quest intake", JsonSerializer.Deserialize<QuestRejectIntakePayload>(entry.Details, _json)?.QuestId),
                var k when k == AuditActions.Quests.Finalize.Key
                    => RenderFinalize(entry, JsonSerializer.Deserialize<QuestFinalizePayload>(entry.Details, _json)),
                var k when k == AuditActions.Quests.ReleaseClaim.Key
                    => RenderReleaseClaim(entry, JsonSerializer.Deserialize<QuestReleaseClaimPayload>(entry.Details, _json)),
                _ => Empty(entry),
            };
        }
        catch (JsonException)
        {
            return Empty(entry);
        }
    }

    private static AuditRender RenderPost(AuditEntryView entry, QuestPostPayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken("Posted "),
            new TextToken($"{p.Origin} ", AuditStyle.Muted),
            new TextToken(p.Name, AuditStyle.Bold),
            new TextToken(" · "),
            new AmountToken(p.Reward, p.CurrencyCode),
            new TextToken($" · {p.Tier}", AuditStyle.Muted),
        };
        return new AuditRender(summary, [Section("Quest",
            ("Name", [new TextToken(p.Name)]),
            ("Origin", [new TextToken(p.Origin.ToString())]),
            ("Tier", [new TextToken(p.Tier.ToString())]),
            ("Reward", [new AmountToken(p.Reward, p.CurrencyCode)]),
            ("Capacity", [new TextToken(p.Capacity.ToString())]),
            ("Starts at", DateTokens(p.StartsAt)),
            ("Deadline", DateTokens(p.Deadline)))]);
    }

    private static AuditRender RenderEdit(AuditEntryView entry, QuestEditPayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken("Edited quest "),
            new CodeToken(p.QuestId.ToString("N")[..8]),
        };
        var rows = new List<(string, IReadOnlyList<AuditToken>)>
        {
            ("Quest", [new CodeToken(p.QuestId.ToString())]),
        };
        if (p.Name is not null) rows.Add(("Name", [new TextToken(p.Name)]));
        if (p.Description is not null) rows.Add(("Description", [new TextToken(p.Description)]));
        if (p.Reward is { } r) rows.Add(("Reward", [new TextToken(r.ToString("N0"))]));
        if (p.Tier is { } t) rows.Add(("Tier", [new TextToken(t.ToString())]));
        if (p.Capacity is { } c) rows.Add(("Capacity", [new TextToken(c.ToString())]));
        if (p.Deadline is { } d) rows.Add(("Deadline", DateTokens(d)));
        return new AuditRender(summary, [Section("Quest edit", [.. rows])]);
    }

    private static AuditRender RenderClaim(AuditEntryView entry, QuestClaimPayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new UserToken(p.UserId),
            new TextToken(" claimed "),
            new CodeToken(p.QuestId.ToString("N")[..8]),
        };
        return new AuditRender(summary, [Section("Claim",
            ("Quest", [new CodeToken(p.QuestId.ToString())]),
            ("Member", [new UserToken(p.UserId)]))]);
    }

    private static AuditRender RenderSubmit(AuditEntryView entry, QuestSubmitPayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new UserToken(p.UserId),
            new TextToken(" submitted "),
            new CodeToken(p.QuestId.ToString("N")[..8]),
        };
        if (!string.IsNullOrWhiteSpace(p.Note))
        {
            summary.Add(new TextToken(" — "));
            summary.Add(new TextToken(p.Note, AuditStyle.Muted));
        }
        return new AuditRender(summary, [Section("Submission",
            ("Quest", [new CodeToken(p.QuestId.ToString())]),
            ("Member", [new UserToken(p.UserId)]),
            ("Note", string.IsNullOrWhiteSpace(p.Note) ? [new TextToken("—", AuditStyle.Muted)] : [new TextToken(p.Note)]))]);
    }

    private static AuditRender RenderReview(AuditEntryView entry, string verb, QuestReviewPayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken($"{verb} "),
            new UserToken(p.MemberId),
            new TextToken(" on "),
            new CodeToken(p.QuestId.ToString("N")[..8]),
        };
        if (!string.IsNullOrWhiteSpace(p.Note))
        {
            summary.Add(new TextToken(" — "));
            summary.Add(new TextToken(p.Note, AuditStyle.Muted));
        }
        return new AuditRender(summary, [Section("Review",
            ("Quest", [new CodeToken(p.QuestId.ToString())]),
            ("Member", [new UserToken(p.MemberId)]),
            ("Note", string.IsNullOrWhiteSpace(p.Note) ? [new TextToken("—", AuditStyle.Muted)] : [new TextToken(p.Note)]))]);
    }

    private static AuditRender RenderDispute(AuditEntryView entry, QuestDisputePayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken("Disputed ", AuditStyle.Warning),
            new CodeToken(p.QuestId.ToString("N")[..8]),
        };
        if (!string.IsNullOrWhiteSpace(p.Reason))
        {
            summary.Add(new TextToken(" — "));
            summary.Add(new TextToken(p.Reason, AuditStyle.Muted));
        }
        return new AuditRender(summary, [Section("Dispute",
            ("Quest", [new CodeToken(p.QuestId.ToString())]),
            ("Reason", string.IsNullOrWhiteSpace(p.Reason) ? [new TextToken("—", AuditStyle.Muted)] : [new TextToken(p.Reason)]))]);
    }

    private static AuditRender RenderArbitrate(AuditEntryView entry, QuestArbitratePayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken("Arbitrated "),
            new CodeToken(p.QuestId.ToString("N")[..8]),
            new TextToken(" — "),
            new TextToken(p.Pay ? "paid completer" : "refunded owner", p.Pay ? AuditStyle.Positive : AuditStyle.Muted),
        };
        return new AuditRender(summary, [Section("Arbitration",
            ("Quest", [new CodeToken(p.QuestId.ToString())]),
            ("Outcome", [new TextToken(p.Pay ? "Paid completer" : "Refunded owner")]))]);
    }

    private static AuditRender RenderRevision(AuditEntryView entry, QuestRevisionPayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken("Requested revision on "),
            new CodeToken(p.QuestId.ToString("N")[..8]),
        };
        if (p.MemberId is { } mid)
        {
            summary.Add(new TextToken(" from "));
            summary.Add(new UserToken(mid));
        }
        return new AuditRender(summary, [Section("Revision request",
            ("Quest", [new CodeToken(p.QuestId.ToString())]),
            ("Member", p.MemberId is { } m ? [new UserToken(m)] : [new TextToken("—", AuditStyle.Muted)]),
            ("Note", string.IsNullOrWhiteSpace(p.Note) ? [new TextToken("—", AuditStyle.Muted)] : [new TextToken(p.Note)]))]);
    }

    private static AuditRender RenderAcceptIntake(AuditEntryView entry, QuestAcceptIntakePayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken("Accepted intake "),
            new CodeToken(p.QuestId.ToString("N")[..8]),
            new TextToken($" · {p.Tier}", AuditStyle.Muted),
        };
        return new AuditRender(summary, [Section("Intake",
            ("Quest", [new CodeToken(p.QuestId.ToString())]),
            ("Assigned tier", [new TextToken(p.Tier.ToString())]),
            ("Require final approval", [new TextToken(p.RequireFinalApproval ? "yes" : "no")]))]);
    }

    private static AuditRender RenderFinalize(AuditEntryView entry, QuestFinalizePayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken("Finalized "),
            new CodeToken(p.QuestId.ToString("N")[..8]),
            new TextToken(" — "),
            new TextToken(p.Pay ? "paid" : "refunded", p.Pay ? AuditStyle.Positive : AuditStyle.Muted),
        };
        return new AuditRender(summary, [Section("Finalization",
            ("Quest", [new CodeToken(p.QuestId.ToString())]),
            ("Outcome", [new TextToken(p.Pay ? "Paid completer" : "Refunded owner")]))]);
    }

    private static AuditRender RenderReleaseClaim(AuditEntryView entry, QuestReleaseClaimPayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken("Released claim on "),
            new CodeToken(p.QuestId.ToString("N")[..8]),
            new TextToken(" from "),
            new UserToken(p.TargetUserId),
        };
        return new AuditRender(summary, [Section("Release claim",
            ("Quest", [new CodeToken(p.QuestId.ToString())]),
            ("Member", [new UserToken(p.TargetUserId)]))]);
    }

    private static AuditRender RenderQuestId(AuditEntryView entry, string verb, Guid? questId)
    {
        if (questId is null) return Empty(entry);
        var id = questId.Value;
        return new AuditRender(
            Summary: [new TextToken($"{verb} "), new CodeToken(id.ToString("N")[..8])],
            Sections: [Section(verb, ("Quest", [new CodeToken(id.ToString())]))]);
    }

    private static IReadOnlyList<AuditToken> DateTokens(DateTimeOffset? d) =>
        d is null ? [new TextToken("—", AuditStyle.Muted)]
                  : [new TextToken(d.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'"))];

    private static AuditSection Section(string heading, params (string Label, IReadOnlyList<AuditToken> Value)[] rows)
        => new(heading, Body: [], Rows: rows.Select(r => new AuditSectionRow(r.Label, r.Value)).ToList());

    private static AuditRender Empty(AuditEntryView entry) =>
        new(
            Summary: [new TextToken(entry.Details ?? "", AuditStyle.Muted)],
            Sections: [new AuditSection("Details", [new TextToken(entry.Details ?? "")])]);
}
