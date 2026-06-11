using Microsoft.Extensions.DependencyInjection;
using Muster.Contracts;
using Muster.Domain.Entities.Guilds;

namespace Muster.Web.Components.Pages.Quests.Admin;

public partial class QuestSettings : IDisposable
{
    private GuildQuestSettings _s = new();
    private IReadOnlyList<Muster.Web.GuildChannelOptions.ChannelOption> _chat = [];

    private enum SaveStatus { None, Saving, Saved, Error }
    private SaveStatus _save = SaveStatus.None;
    private string? _saveMsg;
    private const int DebounceMs = 600;
    private const int HideMs = 8_000;
    private CancellationTokenSource? _debounce, _hide;

    private bool _platformBlocked;
    private string? _platformReason;

    // The rest of the config is moot while quests aren't actually running — lock it when a higher layer blocks them
    // or the guild has the master switch off. The master toggle itself stays editable so it can be turned back on.
    private bool ConfigLocked => _platformBlocked || !_s.QuestsEnabled;

    private string ChannelSummary => _s.QuestChannelId == 0 ? "Pull-only board" : "Live channel board";

    protected override async Task LoadAsync()
    {
        _s = await SettingsStore.GetAsync(GuildId);
        _chat = await Channels.TextAsync(GuildId);

        // A platform kill-switch / plan block means the guild toggle can't take effect — grey it out with a reason.
        await using var scope = Scopes.CreateAsyncScope();
        var verdict = await scope.ServiceProvider.GetRequiredService<Muster.Infrastructure.Services.Platform.IFeatureGate>()
            .EvaluateAsync(GuildId, PlatformFeature.Quests);
        _platformBlocked = !verdict.CanEnable;
        _platformReason = verdict.Reason switch
        {
            FeatureGateReason.NotEntitled => "Not included in this server's plan",
            FeatureGateReason.PlatformDisabled => "Disabled platform-wide",
            _ => null,
        };
    }

    // Debounced autosave — every change schedules a save ~600ms out, superseding any pending one.
    private void ScheduleSave()
    {
        _debounce?.Cancel();
        _hide?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        _save = SaveStatus.Saving;
        _ = InvokeAsync(async () =>
        {
            try { await Task.Delay(DebounceMs, cts.Token); }
            catch (TaskCanceledException) { return; }
            await SaveAsync();
            StateHasChanged();
            ScheduleHide();
        });
    }

    private void ScheduleHide()
    {
        _hide?.Cancel();
        var hide = _hide = new CancellationTokenSource();
        _ = InvokeAsync(async () =>
        {
            try { await Task.Delay(HideMs, hide.Token); }
            catch (TaskCanceledException) { return; }
            _save = SaveStatus.None;
            StateHasChanged();
        });
    }

    private async Task SaveAsync()
    {
        try
        {
            await SettingsStore.UpsertAsync(GuildId, row =>
            {
                row.QuestsEnabled = _s.QuestsEnabled;
                row.QuestChannelId = _s.QuestChannelId;
                row.QuestModChannelId = _s.QuestModChannelId;
                row.BoardRetentionHours = _s.BoardRetentionHours;
                row.DeadlineReminderHours = _s.DeadlineReminderHours;
                row.PersonalQuestIntakeApproval = _s.PersonalQuestIntakeApproval;
                row.AllowSelfParticipation = _s.AllowSelfParticipation;
                row.FinalApprovalMode = _s.FinalApprovalMode;
                row.IntakeTimeoutHours = _s.IntakeTimeoutHours;
                row.IntakeTimeoutAction = _s.IntakeTimeoutAction;
                row.ClaimTimeoutHours = _s.ClaimTimeoutHours;
                row.SubmissionTimeoutHours = _s.SubmissionTimeoutHours;
                row.SubmissionTimeoutAction = _s.SubmissionTimeoutAction;
                row.FinalApprovalTimeoutHours = _s.FinalApprovalTimeoutHours;
                row.FinalApprovalTimeoutAction = _s.FinalApprovalTimeoutAction;
                row.DisputeTimeoutHours = _s.DisputeTimeoutHours;
                row.MaxOpenQuestsPerPoster = _s.MaxOpenQuestsPerPoster;
                row.MaxActiveClaimsPerUser = _s.MaxActiveClaimsPerUser;
                row.MaxRevisions = _s.MaxRevisions;
                row.TierSPoints = _s.TierSPoints;
                row.TierAPoints = _s.TierAPoints;
                row.TierBPoints = _s.TierBPoints;
                row.TierCPoints = _s.TierCPoints;
                row.TierDPoints = _s.TierDPoints;
                row.TierEPoints = _s.TierEPoints;
            });
            await AuditAsync("quest.settings", "Updated quest settings");
            _save = SaveStatus.Saved;
        }
        catch
        {
            _save = SaveStatus.Error;
            _saveMsg = "Couldn't save";
        }
    }

    public void Dispose()
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        _hide?.Cancel();
        _hide?.Dispose();
    }
}
