using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Contracts;
using Muster.Domain;
using Muster.Domain.Entities.Guilds;
using Muster.Domain.Enums;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Quests;

namespace Muster.Web.Components.Pages.Quests;

public partial class QuestDetail : IDisposable
{
    [Parameter] public string QuestId { get; set; } = string.Empty;

    private QuestDetailView? _quest;
    private Guid _questGuid;
    private GuildQuestSettings _settings = new();
    private bool _isManager;
    private FeatureVerdict _gate;
    private bool _isStaff;
    private string _zoneId = TimeZoneService.Utc;
    private bool _acting;
    private string? _actingAction;        // which action is in flight — drives the per-button spinner
    private bool _loading;                // a reload is in flight — drives the top progress bar

    // Quest-level transient action inputs.
    private string _questNote = string.Empty;
    private QuestTier _intakeTier = QuestTier.None;
    private bool _intakeFinal;

    // Per-participant feedback notes (manager review).
    private readonly Dictionary<ulong, string> _participantNotes = [];

    private static readonly QuestTier[] _tiers = [QuestTier.E, QuestTier.D, QuestTier.C, QuestTier.B, QuestTier.A, QuestTier.S];

    private GuildActor Actor => new(GuildId, UserId);

    protected override async Task LoadAsync()
    {
        await using (var scope = Scopes.CreateAsyncScope())
        {
            // Detail is wind-down-gated: an in-flight quest stays viewable when the guild merely has quests off
            // (CanEnable); only a platform/plan block (Unavailable) hides it entirely.
            _gate = await scope.ServiceProvider.GetRequiredService<IFeatureGate>().EvaluateAsync(GuildId, PlatformFeature.Quests);
        }

        if (!_gate.CanEnable)
        {
            return;
        }

        _isManager = await Auth.IsQuestManagerAsync(GuildId, UserId);
        _isStaff = await Auth.IsEconomyManagerAsync(GuildId, UserId); // gates member-detail links (admin-inclusive)
        _zoneId = await TimeZones.ResolveZoneIdAsync(GuildId, UserId);

        if (Guid.TryParse(QuestId, out var id))
        {
            _questGuid = id;
        }

        await ReloadAsync();
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
        {
            Notifier.Updated += OnQuestChanged;
        }
    }

    private void OnQuestChanged(QuestLifecycleNotified change)
    {
        if (State != AccessState.Ready || change.QuestId != _questGuid)
        {
            return;
        }

        _ = InvokeAsync(async () =>
        {
            await ReloadAsync();
            StateHasChanged();
        });
    }

    private async Task ReloadAsync()
    {
        if (_questGuid == Guid.Empty)
        {
            return;
        }

        _loading = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var reads = scope.ServiceProvider.GetRequiredService<IQuestReadService>();
            _settings = await reads.GetSettingsAsync(GuildId);
            var quest = await reads.GetQuestDetailAsync(GuildId, _questGuid);
            // Scrub private fields for non-privileged viewers. Owner/manager/per-row-self keep visibility.
            _quest = quest is null ? null : QuestDetailViewScrub.ForViewer(quest, UserId, _isManager);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task ActAsync(string action, ulong? participantId)
    {
        if (_acting || string.IsNullOrEmpty(action) || _questGuid == Guid.Empty)
        {
            return;
        }

        // Member-suffixed actions (approve:123 …) carry the per-row note; quest-level actions use _questNote.
        string? note = participantId is { } pid
            ? (_participantNotes.GetValueOrDefault(pid)?.Trim() is { Length: > 0 } pn ? pn : null)
            : (_questNote.Trim() is { Length: > 0 } qn ? qn : null);

        _acting = true;
        _actingAction = action;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            var result = await QuestActionRunner.RunAsync(bus, GuildId, _questGuid, UserId, action, note, _intakeTier, _intakeFinal);
            Message = result.Message;

            // Clear the input that was just consumed so the next action starts fresh.
            if (participantId is { } pid2) { _participantNotes.Remove(pid2); }
            else { _questNote = string.Empty; }
        }
        finally
        {
            _acting = false;
            _actingAction = null;
            await ReloadAsync();
        }
    }

    // Per-button spinner: true while the matching action is in flight (the spinner glyph itself is Spinner() in the
    // .razor, since it needs Razor template syntax).
    private bool IsSpinning(string action) => _acting && _actingAction == action;

    public void Dispose()
    {
        Notifier.Updated -= OnQuestChanged;
    }

    private bool Can(QuestPermission action) =>
        _quest is not null && Authorizer.Allows(Actor, _isManager, _quest.Origin, _quest.OwnerId, isTaker: false, action);

    private bool CanIntake(QuestDetailView q) =>
        q.Origin == QuestOrigin.Player && q.Status == QuestStatus.PendingApproval && Can(QuestPermission.AcceptIntake);

    private bool CanFinalize(QuestDetailView q) =>
        q.Origin == QuestOrigin.Player && q.Status == QuestStatus.PendingFinal && Can(QuestPermission.Finalize);

    private bool CanArbitrate(QuestDetailView q, bool iAmOwner, QuestDetailParticipant? myPart) =>
        q.Origin == QuestOrigin.Player && q.Status == QuestStatus.Disputed && Can(QuestPermission.Arbitrate)
        && !iAmOwner && myPart is null; // recusal: a party can't arbitrate their own dispute

    private bool CanCancel(QuestDetailView q) =>
        q.Status is QuestStatus.Open or QuestStatus.Scheduled or QuestStatus.PendingApproval
        && !q.Participants.Any(p => p.Status is QuestParticipantStatus.Claimed or QuestParticipantStatus.Submitted
            or QuestParticipantStatus.RevisionRequested or QuestParticipantStatus.Approved)
        && Can(QuestPermission.Cancel);

    private static bool NoteableQuest(QuestDetailView q, QuestDetailParticipant? myPart, bool iAmOwner, bool isPersonal, bool hasSubmitter) =>
        (q.Status == QuestStatus.Open && myPart?.Status is QuestParticipantStatus.Claimed or QuestParticipantStatus.RevisionRequested) // (re)submit
        || (q.Status == QuestStatus.Open && isPersonal && iAmOwner && hasSubmitter);        // owner revise

    private string Local(DateTimeOffset? utc)
    {
        if (utc is not { } t)
        {
            return "—";
        }

        if (!TimeZoneService.IsValidZone(_zoneId))
        {
            return t.UtcDateTime.ToString("yyyy-MM-dd HH:mm");
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(_zoneId);
        return TimeZoneInfo.ConvertTime(t, zone).ToString("yyyy-MM-dd HH:mm");
    }

    private static string StatusClass(QuestStatus s) => s switch
    {
        QuestStatus.Closed => "chip-review",
        QuestStatus.Cancelled or QuestStatus.Expired or QuestStatus.Disputed => "chip-closed",
        _ => "chip-progress",
    };

    private static string StatusLabel(QuestStatus s) => s switch
    {
        QuestStatus.PendingApproval => "Pending intake",
        QuestStatus.PendingFinal => "Awaiting sign-off",
        QuestStatus.Closed => "Completed",
        _ => s.ToString(),
    };

    /// <summary>Origin-aware lifecycle stages + current index for the header status tracker. Quest-level (per-worker
    /// progress lives in the participants list); cancelled / expired / disputed render the run in a danger tone.</summary>
    private static (IReadOnlyList<string> Stages, int Current, string? Tone) Tracker(QuestDetailView q)
    {
        var hasSub = q.Participants.Any(p => p.Status == QuestParticipantStatus.Submitted);
        var bad = q.Status is QuestStatus.Cancelled or QuestStatus.Expired or QuestStatus.Disputed;

        if (q.Origin == QuestOrigin.Player)
        {
            IReadOnlyList<string> stages = ["Posted", "Intake", "Open", "Settling", "Paid"];
            var cur = q.Status switch
            {
                QuestStatus.Scheduled => 0,
                QuestStatus.PendingApproval => 1,
                QuestStatus.Open => hasSub ? 3 : 2,
                QuestStatus.PendingFinal or QuestStatus.Disputed => 3,
                QuestStatus.Closed => 4,
                _ => 4,
            };
            return (stages, cur, bad ? "danger" : null);
        }

        IReadOnlyList<string> guildStages = ["Posted", "Open", "Reviewing", "Closed"];
        var guildCur = q.Status switch
        {
            QuestStatus.Scheduled => 0,
            QuestStatus.Open => hasSub ? 2 : 1,
            QuestStatus.Closed => 3,
            _ => 3,
        };
        return (guildStages, guildCur, bad ? "danger" : null);
    }

    private static string RingClass(QuestParticipantStatus s) => s switch
    {
        QuestParticipantStatus.Approved => "approved",
        QuestParticipantStatus.Submitted => "submitted",
        QuestParticipantStatus.RevisionRequested => "revision",
        QuestParticipantStatus.Rejected => "rejected",
        _ => "claimed",
    };

    private static string ParticipantChip(QuestParticipantStatus s) => s switch
    {
        QuestParticipantStatus.Approved => "chip-review",
        QuestParticipantStatus.Rejected => "chip-closed",
        _ => "chip-progress",
    };

    private static string ParticipantLabel(QuestParticipantStatus s) => s switch
    {
        QuestParticipantStatus.RevisionRequested => "Revision",
        _ => s.ToString(),
    };
}
