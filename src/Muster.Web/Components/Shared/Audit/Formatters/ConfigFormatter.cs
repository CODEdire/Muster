using System.Text.Json;
using Muster.Infrastructure.Services.Platform;

namespace Muster.Web.Components.Shared.Audit.Formatters;

/// <summary>
/// Configuration-area formatter: role toggles, ledger retention, multiplier add/remove/toggle, tracking +
/// quest settings forms. Each branch maps a payload to a one-line summary plus a structured Rows section.
/// </summary>
public class ConfigFormatter : IAuditFormatter
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> _keys =
    [
        AuditActions.Config.AdminRole.Key, AuditActions.Config.OfficerRole.Key,
        AuditActions.Config.ParticipantRole.Key, AuditActions.Config.QuestManagerRole.Key,
        AuditActions.Config.EconomyManagerRole.Key, AuditActions.Config.EventOfficerRole.Key,
        AuditActions.Config.TrackingManagerRole.Key, AuditActions.Config.AuditorRole.Key,
        AuditActions.Config.LedgerRetention.Key,
        AuditActions.Config.MultiplierAdd.Key, AuditActions.Config.MultiplierRemove.Key,
        AuditActions.Config.MultiplierToggle.Key,
        AuditActions.Config.TrackingSettings.Key,
        AuditActions.Config.TrackChannel.Key, AuditActions.Config.UntrackChannel.Key,
        AuditActions.Config.QuestChannel.Key, AuditActions.Config.QuestAutomation.Key,
        AuditActions.Config.QuestApproval.Key, AuditActions.Config.QuestTierPoints.Key,
    ];

    public bool Matches(string action) => _keys.Contains(action);

    public AuditRender Render(AuditEntryView entry)
    {
        if (string.IsNullOrEmpty(entry.Details)) return Empty(entry);

        try
        {
            // Role toggles all share one payload type.
            if (entry.Action.StartsWith("config.") && entry.Action.EndsWith("Role"))
            {
                return RenderRoleToggle(entry, JsonSerializer.Deserialize<RoleTogglePayload>(entry.Details, _json));
            }

            return entry.Action switch
            {
                var k when k == AuditActions.Config.LedgerRetention.Key
                    => RenderLedgerRetention(entry, JsonSerializer.Deserialize<LedgerRetentionPayload>(entry.Details, _json)),
                var k when k == AuditActions.Config.MultiplierAdd.Key
                    => RenderMultiplierAdded(entry, JsonSerializer.Deserialize<MultiplierPayload>(entry.Details, _json)),
                var k when k == AuditActions.Config.MultiplierRemove.Key
                    => RenderMultiplierRemoved(entry, JsonSerializer.Deserialize<MultiplierPayload>(entry.Details, _json)),
                var k when k == AuditActions.Config.MultiplierToggle.Key
                    => RenderMultiplierToggle(entry, JsonSerializer.Deserialize<MultiplierTogglePayload>(entry.Details, _json)),
                var k when k == AuditActions.Config.TrackingSettings.Key
                    => RenderTrackingSettings(entry, JsonSerializer.Deserialize<TrackingSettingsPayload>(entry.Details, _json)),
                var k when k == AuditActions.Config.TrackChannel.Key
                    => RenderTrackChannel(entry, JsonSerializer.Deserialize<TrackedChannelPayload>(entry.Details, _json)),
                var k when k == AuditActions.Config.UntrackChannel.Key
                    => RenderUntrackChannel(entry, JsonSerializer.Deserialize<TrackedChannelRemovePayload>(entry.Details, _json)),
                var k when k == AuditActions.Config.QuestChannel.Key
                    => RenderQuestChannel(entry, JsonSerializer.Deserialize<QuestChannelPayload>(entry.Details, _json)),
                var k when k == AuditActions.Config.QuestAutomation.Key
                    => RenderQuestAutomation(entry, JsonSerializer.Deserialize<QuestAutomationPayload>(entry.Details, _json)),
                var k when k == AuditActions.Config.QuestApproval.Key
                    => RenderQuestApproval(entry, JsonSerializer.Deserialize<QuestApprovalPayload>(entry.Details, _json)),
                var k when k == AuditActions.Config.QuestTierPoints.Key
                    => RenderQuestTierPoints(entry, JsonSerializer.Deserialize<QuestTierPointsPayload>(entry.Details, _json)),
                _ => Empty(entry),
            };
        }
        catch (JsonException)
        {
            return Empty(entry);
        }
    }

    private static AuditRender RenderRoleToggle(AuditEntryView entry, RoleTogglePayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken(p.Added ? "Added " : "Removed "),
            new RoleToken(p.RoleId),
            new TextToken($" as {p.Kind}", AuditStyle.Muted),
        };
        return new AuditRender(summary, [Section("Role mapping",
            ("Kind", [new TextToken(p.Kind)]),
            ("Role", [new RoleToken(p.RoleId)]),
            ("Action", [new TextToken(p.Added ? "added" : "removed")]))]);
    }

    private static AuditRender RenderLedgerRetention(AuditEntryView entry, LedgerRetentionPayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken("Ledger retention "),
            new CodeToken($"{p.OldDays}d"),
            new TextToken(" → "),
            new CodeToken($"{p.NewDays}d"),
        };
        return new AuditRender(summary, [Section("Ledger retention",
            ("Before", [new TextToken(p.OldDays == 0 ? "platform default" : $"{p.OldDays} days")]),
            ("After", [new TextToken(p.NewDays == 0 ? "platform default" : $"{p.NewDays} days")]))]);
    }

    private static AuditRender RenderMultiplierAdded(AuditEntryView entry, MultiplierPayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken("Added multiplier "),
            new TextToken(p.Name, AuditStyle.Bold),
            new TextToken(" "),
            new CodeToken($"{p.Factor:0.##}×"),
            new TextToken(" "),
            new TextToken(p.Scope.ToString(), AuditStyle.Muted),
        };
        return new AuditRender(summary, [Section("Multiplier",
            ("Name", [new TextToken(p.Name)]),
            ("Factor", [new TextToken($"{p.Factor:0.##}×")]),
            ("Scope", [new TextToken(p.Scope.ToString())]))]);
    }

    private static AuditRender RenderMultiplierRemoved(AuditEntryView entry, MultiplierPayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken("Removed multiplier "),
            new TextToken(p.Name, AuditStyle.Bold),
            new TextToken(" "),
            new CodeToken($"{p.Factor:0.##}×"),
        };
        return new AuditRender(summary, [Section("Multiplier",
            ("Name", [new TextToken(p.Name)]),
            ("Factor", [new TextToken($"{p.Factor:0.##}×")]),
            ("Scope", [new TextToken(p.Scope.ToString())]))]);
    }

    private static AuditRender RenderMultiplierToggle(AuditEntryView entry, MultiplierTogglePayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken(p.Enabled ? "Enabled multiplier " : "Disabled multiplier "),
            new CodeToken(p.MultiplierId.ToString("N")[..8]),
        };
        return new AuditRender(summary, [Section("Multiplier toggle",
            ("Multiplier", [new CodeToken(p.MultiplierId.ToString())]),
            ("State", [new TextToken(p.Enabled ? "enabled" : "disabled",
                p.Enabled ? AuditStyle.Positive : AuditStyle.Muted)]))]);
    }

    private static AuditRender RenderTrackingSettings(AuditEntryView entry, TrackingSettingsPayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken("Updated tracking settings "),
            new TextToken($"(coin {p.CoinCurrency ?? "—"}, {p.MinutesPerCoin}m/coin)", AuditStyle.Muted),
        };
        return new AuditRender(summary, [Section("Tracking settings",
            ("Background opt-in", [new TextToken(p.BackgroundOptIn ? "yes" : "no")]),
            ("Apply guards to sessions", [new TextToken(p.ApplyGuardsToSessions ? "yes" : "no")]),
            ("Session coin currency", [new TextToken(p.CoinCurrency ?? "—")]),
            ("Minutes per coin", [new TextToken(p.MinutesPerCoin.ToString())]),
            ("Max session hours", [new TextToken(p.MaxSessionHours.ToString())]),
            ("Activity retention (days)", [new TextToken(p.ActivityRetentionDays.ToString())]),
            ("Min tracked seconds", [new TextToken(p.MinTrackedSeconds.ToString())]),
            ("Stacking", [new TextToken(p.Stacking.ToString())]),
            ("Cap", [new TextToken(p.Cap?.ToString("0.##") ?? "—")]),
            ("Start bonus", [new TextToken($"{p.StartBonus:0.##}× × {p.StartWindowMinutes}m")]),
            ("End bonus", [new TextToken($"{p.EndBonus:0.##}× × {p.EndWindowMinutes}m")]),
            ("Multiply bonuses", [new TextToken(p.MultiplyBonuses ? "yes" : "no")]))]);
    }

    private static AuditRender RenderTrackChannel(AuditEntryView entry, TrackedChannelPayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken("Tracked "),
            new ChannelToken(p.ChannelId),
            new TextToken($" ({p.Kind} / {p.Mode})", AuditStyle.Muted),
        };
        return new AuditRender(summary, [Section("Tracked channel",
            ("Channel", [new ChannelToken(p.ChannelId), new TextToken(p.ChannelName is null ? "" : $" — {p.ChannelName}", AuditStyle.Muted)]),
            ("Kind", [new TextToken(p.Kind.ToString())]),
            ("Mode", [new TextToken(p.Mode.ToString())]))]);
    }

    private static AuditRender RenderUntrackChannel(AuditEntryView entry, TrackedChannelRemovePayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken("Untracked "),
            new ChannelToken(p.ChannelId),
        };
        return new AuditRender(summary, [Section("Untracked channel",
            ("Channel", [new ChannelToken(p.ChannelId), new TextToken(p.ChannelName is null ? "" : $" — {p.ChannelName}", AuditStyle.Muted)]))]);
    }

    private static AuditRender RenderQuestChannel(AuditEntryView entry, QuestChannelPayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken("Quest board channel "),
            p.BoardChannelId is { } board ? new ChannelToken(board) : new TextToken("—", AuditStyle.Muted),
        };
        return new AuditRender(summary, [Section("Quest board",
            ("Board channel", [p.BoardChannelId is { } b ? new ChannelToken(b) : new TextToken("—", AuditStyle.Muted)]),
            ("Mod channel", [p.ModChannelId is { } m ? new ChannelToken(m) : new TextToken("—", AuditStyle.Muted)]),
            ("Retention (hours)", [new TextToken(p.RetentionHours.ToString())]))]);
    }

    private static AuditRender RenderQuestAutomation(AuditEntryView entry, QuestAutomationPayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken("Updated quest automation "),
            new TextToken($"(intake {p.IntakeHours}h / submission {p.SubmissionHours}h)", AuditStyle.Muted),
        };
        return new AuditRender(summary, [Section("Quest automation",
            ("Intake timeout", [new TextToken($"{p.IntakeHours}h → {p.IntakeAction}")]),
            ("Claim timeout", [new TextToken($"{p.ClaimHours}h → released")]),
            ("Submission timeout", [new TextToken($"{p.SubmissionHours}h → {p.SubmissionAction}")]),
            ("Final timeout", [new TextToken($"{p.FinalHours}h → {p.FinalAction}")]),
            ("Max open per poster", [new TextToken(p.MaxOpenPerPoster.ToString())]),
            ("Max active claims", [new TextToken(p.MaxActiveClaims.ToString())]),
            ("Max revisions", [new TextToken(p.MaxRevisions.ToString())]),
            ("Deadline reminder", [new TextToken($"{p.DeadlineReminderHours}h before")]))]);
    }

    private static AuditRender RenderQuestApproval(AuditEntryView entry, QuestApprovalPayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken("Updated quest approval "),
            new TextToken($"(intake {(p.RequireIntakeApproval ? "manual" : "auto")}, final {p.FinalMode})", AuditStyle.Muted),
        };
        return new AuditRender(summary, [Section("Quest approval",
            ("Intake approval", [new TextToken(p.RequireIntakeApproval ? "required" : "auto-accept")]),
            ("Final approval mode", [new TextToken(p.FinalMode.ToString())]),
            ("Self review", [new TextToken(p.AllowSelfReview ? "allowed" : "blocked")]))]);
    }

    private static AuditRender RenderQuestTierPoints(AuditEntryView entry, QuestTierPointsPayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken("Updated tier points "),
            new TextToken($"S={p.S} A={p.A} B={p.B} C={p.C} D={p.D} E={p.E}", AuditStyle.Muted),
        };
        return new AuditRender(summary, [Section("Tier points",
            ("S", [new TextToken(p.S.ToString("N0"))]),
            ("A", [new TextToken(p.A.ToString("N0"))]),
            ("B", [new TextToken(p.B.ToString("N0"))]),
            ("C", [new TextToken(p.C.ToString("N0"))]),
            ("D", [new TextToken(p.D.ToString("N0"))]),
            ("E", [new TextToken(p.E.ToString("N0"))]))]);
    }

    private static AuditSection Section(string heading, params (string Label, IReadOnlyList<AuditToken> Value)[] rows)
        => new(heading, Body: [], Rows: rows.Select(r => new AuditSectionRow(r.Label, r.Value)).ToList());

    private static AuditRender Empty(AuditEntryView entry) =>
        new(
            Summary: [new TextToken(entry.Details ?? "", AuditStyle.Muted)],
            Sections: [new AuditSection("Details", [new TextToken(entry.Details ?? "")])]);
}
