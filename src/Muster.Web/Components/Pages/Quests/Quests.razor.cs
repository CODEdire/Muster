using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Contracts;
using Muster.Domain;
using Muster.Domain.Entities.Guilds;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Quests;
using Muster.Web.Components.Shared;
using Muster.Web.Components.Pages.Quests.Models;
using static Muster.Web.Components.Pages.Quests.Core.QuestPresentation;

namespace Muster.Web.Components.Pages.Quests;

public partial class Quests : IDisposable
{
    [SupplyParameterFromQuery] private string? Tab { get; set; }
    [SupplyParameterFromQuery] private string? Search { get; set; }
    [SupplyParameterFromQuery] private string? Type { get; set; }
    [SupplyParameterFromQuery] private string? Sort { get; set; }
    [SupplyParameterFromQuery] private bool Desc { get; set; }
    [SupplyParameterFromQuery] private int Page { get; set; }
    [SupplyParameterFromQuery] private int Size { get; set; }
    [SupplyParameterFromQuery(Name = "qtype")] private string? QType { get; set; }  // quest-type filter (type id)
    [SupplyParameterFromQuery(Name = "qtier")] private string? QTier { get; set; }  // difficulty-tier filter

    private static readonly int[] PageSizes = [10, 25, 50, 100];

    private IReadOnlyList<QuestBoardItem> _quests = [];
    private IReadOnlyDictionary<Guid, string> _codes = new Dictionary<Guid, string>();
    private IReadOnlyDictionary<ulong, string> _names = new Dictionary<ulong, string>();
    private IReadOnlyDictionary<ulong, string> _avatars = new Dictionary<ulong, string>();
    private bool _isManager;
    private bool _isStaff;
    private string _zoneId = TimeZoneService.Utc;
    private GuildQuestSettings _settings = new();
    private FeatureVerdict _gate;
    private IReadOnlyList<Muster.Domain.Entities.Quests.QuestType> _types = [];
    private Dictionary<Guid, Muster.Domain.Entities.Quests.QuestType> _typeById = new();
    private QuestCardData _cardData = QuestCardData.Empty;   // per-board lookup bundle for QuestCard + table view
    private string _view = "grid"; // board layout: grid (cards) or list (single column)
    private int _total;
    private int _totalPages;
    private int _page = 1;
    private bool _loading;
    private string? _appliedKey;          // last-applied view key; reload only when the effective view changes
    private Guid? _acting;                // quest whose action is in flight (disables its buttons)
    private string? _actingAction;        // the specific action — drives the per-button spinner

    private const int SearchDebounceMs = 350;
    private string? _searchBox;           // live input value, debounced into the Search query param
    private CancellationTokenSource? _searchDebounce;

    // Per-card transient action inputs (interactive — no form post): note / intake tier / final-approval toggle.
    private readonly Dictionary<Guid, string> _notes = [];
    private readonly Dictionary<Guid, QuestTier> _intakeTier = [];
    private readonly Dictionary<Guid, bool> _intakeFinal = [];

    private static readonly QuestTier[] _tiers = [QuestTier.E, QuestTier.D, QuestTier.C, QuestTier.B, QuestTier.A, QuestTier.S];

    // Button visibility shares the handler's authorization rule via IQuestAuthorizer.Allows (no extra DB calls —
    // _isManager is resolved once in LoadAsync); the state conditions (status / no-active-participant) stay here.
    private GuildActor Actor => new(GuildId, UserId);

    private bool Can(QuestBoardItem q, QuestPermission action) =>
        Authorizer.Allows(Actor, _isManager, q.Origin, q.OwnerId, isTaker: false, action);

    /// <summary>Managers vet + tier a pending personal quest at intake.</summary>
    private bool CanIntake(QuestBoardItem q) =>
        q.Origin == QuestOrigin.Player && q.Status == QuestStatus.PendingApproval && Can(q, QuestPermission.AcceptIntake);

    /// <summary>Managers give the final sign-off on a personal quest awaiting it.</summary>
    private bool CanFinalize(QuestBoardItem q) =>
        q.Origin == QuestOrigin.Player && q.Status == QuestStatus.PendingFinal && Can(q, QuestPermission.Finalize);

    /// <summary>Editable only before anyone is working on it: owner (personal) or manager (guild).</summary>
    private bool CanEdit(QuestBoardItem q) =>
        q.Status is QuestStatus.Open or QuestStatus.Scheduled or QuestStatus.PendingApproval
        && !q.Participants.Any(p => p.Status is QuestParticipantStatus.Claimed or QuestParticipantStatus.Submitted
            or QuestParticipantStatus.RevisionRequested or QuestParticipantStatus.Approved)
        && Can(q, QuestPermission.Edit);

    /// <summary>Whether any available action benefits from an optional note (submit/revise/reject).</summary>
    private static bool Noteable(IReadOnlyList<QuestAction> actions) =>
        actions.Any(a => a.Value is "submit" or "revise"
            || a.Value.StartsWith("revise:", StringComparison.Ordinal)
            || a.Value.StartsWith("reject:", StringComparison.Ordinal));

    private string CurrentTab => Tab is "actionneeded" or "history" ? Tab : "active";
    private string CurrentType => Type is "guild" or "player" or "mine" ? Type : "";
    private string CurrentSort => string.IsNullOrEmpty(Sort) ? "created" : Sort;
    private bool CurrentDesc => string.IsNullOrEmpty(Sort) ? true : Desc;
    private int CurrentSize => PageSizes.Contains(Size) ? Size : 25;

    private Guid? CurrentTypeId => Guid.TryParse(QType, out var g) ? g : null;
    private QuestTier? CurrentTierFilter => Enum.TryParse<QuestTier>(QTier, out var t) && t != QuestTier.None ? t : null;
    private bool HasFilters => !string.IsNullOrEmpty(Search) || CurrentType.Length > 0 || !string.IsNullOrEmpty(QType) || !string.IsNullOrEmpty(QTier);
    private string? TypeFilterName => CurrentTypeId is { } id && _typeById.TryGetValue(id, out var t) ? t.Name : null;

    // One-time gate setup (manager flag + zone); the board itself loads from the query in OnParametersSetAsync.
    protected override async Task LoadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        _gate = await scope.ServiceProvider.GetRequiredService<IFeatureGate>().EvaluateAsync(GuildId, PlatformFeature.Quests);
        if (!_gate.IsEnabled)
        {
            return; // gated — the board is hidden; skip role lookups
        }

        _isManager = await Auth.IsQuestManagerAsync(GuildId, UserId);
        _isStaff = await Auth.IsEconomyManagerAsync(GuildId, UserId); // gates member-detail links (admin-inclusive)
        _zoneId = await TimeZones.ResolveZoneIdAsync(GuildId, UserId);

        var types = await scope.ServiceProvider.GetRequiredService<IQuestReadService>().GetQuestTypesAsync(GuildId);
        _types = types;
        _typeById = types.ToDictionary(t => t.Id);
    }

    // Table-view display helpers delegate to the shared QuestCardData (same source the cards use).
    private string TypeIcon(Guid? typeId) => _cardData.TypeIcon(typeId);
    private string? TypeName(Guid? typeId) => _cardData.TypeName(typeId);

    // Fires on first render (after the gate) and on every query change (SPA nav). Reload only when the view changed.
    protected override async Task OnParametersSetAsync()
    {
        if (State != AccessState.Ready || !_gate.IsEnabled)
        {
            return;
        }

        _searchBox = Search; // keep the box in sync with the query on initial load / external nav
        var key = $"{CurrentTab}|{CurrentType}|{Search}|{CurrentSort}|{CurrentDesc}|{Page}|{CurrentSize}|{QType}|{QTier}";
        if (key == _appliedKey)
        {
            return;
        }

        _appliedKey = key;
        await ReloadAsync();
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
        {
            Notifier.Updated += OnQuestChanged;
        }
    }

    // A live quest change in this guild refreshes the board (no URL change). Fans in from any origin (web/bot/api/sweep).
    private void OnQuestChanged(QuestLifecycleNotified change)
    {
        if (State != AccessState.Ready || change.GuildId != GuildId)
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
        _loading = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var board = await scope.ServiceProvider.GetRequiredService<IQuestReadService>()
                .GetBoardAsync(GuildId, UserId, _isManager, CurrentTab, CurrentType, Search, CurrentSort, CurrentDesc, Page, CurrentSize,
                    CurrentTypeId, CurrentTierFilter);
            _quests = board.Items;
            _total = board.Total;
            _page = board.Page;
            _totalPages = board.TotalPages;
            _codes = board.Codes;
            _names = board.Names;
            _avatars = board.Avatars;
            _settings = board.Settings;
            _cardData = new QuestCardData(GuildId, _isManager, _isStaff, _zoneId, _settings, _codes, _names, _avatars, _typeById);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task ActAsync(Guid questId, string action)
    {
        if (_acting is not null || string.IsNullOrEmpty(action))
        {
            return;
        }

        var note = _notes.GetValueOrDefault(questId)?.Trim();
        note = string.IsNullOrEmpty(note) ? null : note;
        var tier = _intakeTier.GetValueOrDefault(questId, QuestTier.None);
        var requireFinal = _intakeFinal.GetValueOrDefault(questId);

        _acting = questId;
        _actingAction = action;
        try
        {
            // Every quest action is a command — auditing happens in the command's audit middleware.
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            var result = await QuestActionRunner.RunAsync(bus, GuildId, questId, UserId, action, note, tier, requireFinal);
            Message = result.Message;
            _notes.Remove(questId);
        }
        finally
        {
            _acting = null;
            _actingAction = null;
            await ReloadAsync();
        }
    }

    // Per-button spinner: swap the icon for a spinning `progress_activity` while the matching action is in flight.
    private bool IsSpinning(Guid id, string action) => _acting == id && _actingAction == action;
    private string BtnIcon(Guid id, string action) => IsSpinning(id, action) ? "progress_activity" : IconFor(action);
    private string BtnIconClass(Guid id, string action) => IsSpinning(id, action) ? "material-symbols-outlined spin" : "material-symbols-outlined";

    // ---- Action modal (text / tier input) ---------------------------------------------------------------
    private QuestBoardItem? _modalQuest;
    private string _modalAction = "";
    private string _modalNote = "";
    private QuestTier _modalTier = QuestTier.None;
    private bool _modalFinal;
    private string _modalTitle = "";
    private string _modalNoteLabel = "Note";
    private string _modalNotePlaceholder = "";
    private string _modalConfirm = "Confirm";

    /// <summary>Decide whether an action needs the modal (note / tier) or runs immediately.</summary>
    private void TriggerAction(QuestBoardItem q, string action)
    {
        var key = action.Contains(':') ? action[..action.IndexOf(':')] : action;
        if (action == "accept" || key is "submit" or "revise" or "reject")
        {
            OpenActionModal(q, action);
        }
        else
        {
            _ = ActAsync(q.Id, action);
        }
    }

    private void OpenActionModal(QuestBoardItem q, string action)
    {
        _modalQuest = q;
        _modalAction = action;
        _modalNote = NoteFor(q.Id);
        _modalTier = IntakeTier(q.Id);
        _modalFinal = IntakeFinal(q.Id);
        var key = action.Contains(':') ? action[..action.IndexOf(':')] : action;
        (_modalTitle, _modalNoteLabel, _modalNotePlaceholder, _modalConfirm) = key switch
        {
            "submit" => ($"Submit “{q.Name}”", "Note for the reviewer (optional)", "What you completed…", "Submit"),
            "revise" => ("Request a revision", "What to fix (optional)", "Tell the worker what to change…", "Send back"),
            "reject" => ("Reject submission", "Reason (optional)", "Why it's rejected…", "Reject"),
            "accept" => ($"Accept “{q.Name}”", "", "", "Accept & open"),
            _ => ("Confirm", "Note", "", "Confirm"),
        };
    }

    private void CloseActionModal() => _modalQuest = null;

    private async Task ConfirmActionModal()
    {
        if (_modalQuest is not { } q)
        {
            return;
        }

        _notes[q.Id] = _modalNote;
        _intakeTier[q.Id] = _modalTier;
        _intakeFinal[q.Id] = _modalFinal;
        var (id, action) = (q.Id, _modalAction);
        CloseActionModal();
        await ActAsync(id, action);
    }

    // ---- Control handlers: each rewrites the query (SPA nav); defaults omitted to keep links clean. ----

    private void OnTab(string tab) => Nav.NavigateTo($"{QPath}?{BuildQuery(tab, CurrentType, CurrentSort, CurrentDesc, 1, Search)}", forceLoad: false);

    private void OnType(string type) => Nav.NavigateTo($"{QPath}?{BuildQuery(CurrentTab, type, CurrentSort, CurrentDesc, 1, Search)}", forceLoad: false);

    private void OnTypeSelect(ChangeEventArgs e) => OnType(e.Value as string ?? "");

    // Quest-type + tier filters live in their own query params (qtype / qtier); merge them into the current URI so
    // they compose with the tab / scope / sort / search state. BuildQuery carries them forward on other nav.
    private void OnQuestTypeFilter(ChangeEventArgs e) => SetQuestType(NullIfEmpty(e.Value as string));
    private void OnTierFilter(ChangeEventArgs e) => SetTier(NullIfEmpty(e.Value as string));

    private void SetQuestType(string? id) =>
        Nav.NavigateTo(Nav.GetUriWithQueryParameters(new Dictionary<string, object?> { ["qtype"] = id, ["page"] = null }), forceLoad: false);

    private void SetTier(string? tier) =>
        Nav.NavigateTo(Nav.GetUriWithQueryParameters(new Dictionary<string, object?> { ["qtier"] = tier, ["page"] = null }), forceLoad: false);

    private void OnSearchClear()
    {
        _searchBox = "";
        Nav.NavigateTo($"{QPath}?{BuildQuery(CurrentTab, CurrentType, CurrentSort, CurrentDesc, 1, null)}", forceLoad: false, replace: true);
    }

    private void ClearFilters()
    {
        _searchBox = "";
        Nav.NavigateTo(Nav.GetUriWithQueryParameters(new Dictionary<string, object?>
        {
            ["search"] = null, ["type"] = null, ["qtype"] = null, ["qtier"] = null, ["page"] = null,
        }), forceLoad: false);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private static string OriginLabel(string type) => type switch { "guild" => "Guild", "player" => "Player", "mine" => "Mine", _ => type };

    private void OnSortChange(ChangeEventArgs e)
    {
        if (e.Value is string url && !string.IsNullOrEmpty(url))
        {
            Nav.NavigateTo(url, forceLoad: false);
        }
    }

    // Filter-as-you-type: each keystroke updates _searchBox; wait for a typing pause, then navigate (replace so a
    // search doesn't bury the page in back-button history).
    private async Task DebouncedSearchAsync()
    {
        _searchDebounce?.Cancel();
        var cts = _searchDebounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(SearchDebounceMs, cts.Token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (!cts.IsCancellationRequested)
        {
            var search = string.IsNullOrWhiteSpace(_searchBox) ? null : _searchBox;
            Nav.NavigateTo($"{QPath}?{BuildQuery(CurrentTab, CurrentType, CurrentSort, CurrentDesc, 1, search)}", forceLoad: false, replace: true);
        }
    }

    // ---- Per-card action inputs ----
    private string NoteFor(Guid id) => _notes.GetValueOrDefault(id, "");
    private void SetNote(Guid id, ChangeEventArgs e) => _notes[id] = e.Value as string ?? "";
    private QuestTier IntakeTier(Guid id) => _intakeTier.GetValueOrDefault(id, QuestTier.None);
    private void SetIntakeTier(Guid id, ChangeEventArgs e) => _intakeTier[id] = Enum.TryParse<QuestTier>(e.Value as string, out var t) ? t : QuestTier.None;
    private bool IntakeFinal(Guid id) => _intakeFinal.GetValueOrDefault(id);
    private void SetIntakeFinal(Guid id, ChangeEventArgs e) => _intakeFinal[id] = e.Value is true;

    public void Dispose()
    {
        Notifier.Updated -= OnQuestChanged;
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
    }

    /// <summary>The actions this viewer may take on a quest, given its state and their role.</summary>
    private IEnumerable<QuestAction> ActionsFor(QuestBoardItem q)
    {
        var myPart = q.Participants.FirstOrDefault(p => p.UserId == UserId);
        var iAmOwner = q.OwnerId == UserId;
        var submitters = q.Participants.Where(p => p.Status == QuestParticipantStatus.Submitted).ToList();
        var isPersonal = q.Origin == QuestOrigin.Player;

        if (q.Status == QuestStatus.Open)
        {
            // Guild quests are guild-owned, so the creator may claim. A player bounty's owner can't take it
            // unless the guild enabled self-participation.
            var canClaim = myPart is null && (!isPersonal || !iAmOwner || _settings.AllowSelfParticipation);
            if (canClaim)
            {
                yield return new("claim", "Claim", true);
            }

            if (myPart?.Status is QuestParticipantStatus.Claimed or QuestParticipantStatus.RevisionRequested)
            {
                yield return new("submit", myPart.Status == QuestParticipantStatus.RevisionRequested ? "Resubmit" : "Submit", true);
            }

            if (submitters.Count > 0 && isPersonal && iAmOwner)
            {
                yield return new("confirm", "Confirm & pay", true);
                yield return new("revise", "Request revision", false);
                yield return new("dispute", "Dispute", false);
            }
            else if (submitters.Count > 0 && !isPersonal && _isManager)
            {
                foreach (var s in submitters)
                {
                    yield return new($"approve:{s.UserId}", $"Approve {Name(s.UserId)}", true);
                    yield return new($"revise:{s.UserId}", $"Revise {Name(s.UserId)}", false);
                    yield return new($"reject:{s.UserId}", $"Reject {Name(s.UserId)}", false);
                }
            }
        }

        if (q.Status is QuestStatus.Open or QuestStatus.Scheduled or QuestStatus.PendingApproval && submitters.Count == 0
            && ((isPersonal && iAmOwner) || (!isPersonal && _isManager)))
        {
            yield return new("cancel", "Cancel", false);
        }

        if (q.Status == QuestStatus.Disputed && isPersonal && _isManager)
        {
            yield return new("resolve-pay", "Pay completer", true);
            yield return new("resolve-refund", "Refund owner", false);
        }
    }

    private string Name(ulong userId) => _cardData.Name(userId);

    private string? Avatar(ulong userId) => _cardData.Avatar(userId);

    /// <summary>A Material Symbol name for the card's icon toolbar; the full label lives in the button's title/aria-label.</summary>
    private static string IconFor(string action)
    {
        var key = action.Contains(':') ? action[..action.IndexOf(':')] : action;
        return key switch
        {
            "claim" => "add_task",
            "submit" => "send",
            "approve" or "accept" => "thumb_up",          // approval decision
            "reject" or "reject-intake" => "thumb_down",  // denial decision
            "confirm" or "finalize-pay" or "resolve-pay" => "paid", // pay out
            "finalize-refund" or "resolve-refund" => "undo",        // give escrow back
            "revise" => "rate_review",                    // send back for changes
            "reopen" => "replay",
            "cancel" => "delete",                         // withdraw the quest
            "dispute" => "gavel",
            _ => "circle",
        };
    }

    /// <summary>Semantic colour class for an action button (green approve, red deny, amber caution).</summary>
    private static string IconColor(string action)
    {
        var key = action.Contains(':') ? action[..action.IndexOf(':')] : action;
        return key switch
        {
            "approve" or "accept" or "confirm" or "finalize-pay" or "resolve-pay" => "success",
            "reject" or "reject-intake" or "cancel" => "danger",
            "revise" or "dispute" or "finalize-refund" or "resolve-refund" => "warn",
            "claim" or "submit" => "primary",
            _ => "",
        };
    }

    private string QPath => $"/guilds/{GuildId}/quests";

    private string TabUrl(string tab) => $"{QPath}?{BuildQuery(tab, CurrentType, CurrentSort, CurrentDesc, 1, Search)}";

    private string TypeUrl(string type) => $"{QPath}?{BuildQuery(CurrentTab, type, CurrentSort, CurrentDesc, 1, Search)}";

    private string SortValueUrl(string sort, bool desc) => $"{QPath}?{BuildQuery(CurrentTab, CurrentType, sort, desc, 1, Search)}";

    private string PageUrl(int page) => $"{QPath}?{BuildQuery(CurrentTab, CurrentType, CurrentSort, CurrentDesc, page, Search)}";

    /// <summary>Summary tiles for the board header — the current page's mission tally.</summary>
    private IReadOnlyList<PageHeader.KpiItem> SummaryKpis
    {
        get
        {
            var open = _quests.Count(q => q.Status == QuestStatus.Open && !HasActiveWorker(q));
            var inProgress = _quests.Count(q => q.Status == QuestStatus.Open && HasActiveWorker(q));
            var review = _quests.Count(q => q.Participants.Any(p => p.Status == QuestParticipantStatus.Submitted));
            return
            [
                new($"{open}", "Open"),
                new($"{inProgress}", "In progress"),
                new($"{review}", "Awaiting review", review > 0 ? "warn" : null),
            ];
        }
    }

    /// <summary>Whether this quest has any available action — drives whether the card reserves its action bar.</summary>
    private bool HasAnyAction(QuestBoardItem q) =>
        ActionsFor(q).Any() || CanIntake(q) || CanFinalize(q) || CanEdit(q);

    // Display helpers that need the per-board lookups delegate to the shared QuestCardData (pure ones — counts,
    // chips, status — live in the static QuestPresentation, brought in via `@using static`).
    private string DueText(QuestBoardItem q) => _cardData.DueText(q);

    private string BuildQuery(string tab, string type, string sort, bool desc, int page, string? search)
    {
        var parts = new List<string> { $"tab={tab}" };
        if (!string.IsNullOrEmpty(search)) parts.Add($"search={Uri.EscapeDataString(search)}");
        if (!string.IsNullOrEmpty(type)) parts.Add($"type={type}");
        parts.Add($"sort={sort}");
        parts.Add($"desc={desc.ToString().ToLowerInvariant()}");
        parts.Add($"page={page}");
        parts.Add($"size={CurrentSize}");
        if (!string.IsNullOrEmpty(QType)) parts.Add($"qtype={QType}");       // preserve the active type/tier filters
        if (!string.IsNullOrEmpty(QTier)) parts.Add($"qtier={QTier}");       // across tab/scope/sort/page/search nav
        return string.Join("&", parts);
    }

    private sealed record QuestAction(string Value, string Label, bool Primary);
}
