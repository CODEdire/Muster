using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Tracking;
using Muster.Infrastructure.Services.Web;
using static Muster.Web.Components.Shared.LedgerMeta;

namespace Muster.Web.Components.Pages.Economy;

public partial class WalletPoints : IDisposable
{
    private const int SearchDebounceMs = 350;

    [SupplyParameterFromQuery(Name = "member")] private string? MemberRaw { get; set; }
    private ulong _targetUserId;
    private bool _viewingOther;
    private bool _canManage;

    private string _displayName = "";
    private string? _avatarUrl;
    private long _balance;
    private bool _seasonal;
    private string? _seasonName;
    private bool _hasPoints = true;

    // Ledger state.
    private PagedResult<MemberLedgerRow>? _activity;
    private (long In, long Out) _totals;
    private int _page = 1;
    private int _size = 25;
    private string? _searchBox;
    private string _preset = "";
    private CurrencyLedgerSource? _source;
    private int? _sign;
    private string _sortKey = "when";
    private bool _descending = true;
    private long? _expandedId;
    private bool _loading;
    private CancellationTokenSource? _searchDebounce;

    private int TotalPages => _activity?.TotalPages ?? 1;
    private string PagerLabel => $"Page {_page} of {TotalPages} · {(_activity?.Total ?? 0)} total";
    private bool Grouped => _sortKey == "when";

    protected override async Task LoadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var points = sp.GetRequiredService<PointsReadService>();
        var members = sp.GetRequiredService<WebMemberService>();

        _canManage = await Auth.IsEconomyManagerAsync(GuildId, UserId);
        var canViewOthers = _canManage || await Auth.IsAuditorAsync(GuildId, UserId);
        _targetUserId = ulong.TryParse(MemberRaw, out var m) && canViewOthers ? m : UserId;
        _viewingOther = _targetUserId != UserId;

        var snap = await points.GetSnapshotAsync(GuildId, _targetUserId);
        _balance = snap.Balance;
        _seasonal = snap.Seasonal;
        _hasPoints = (await points.GetSupplyAsync(GuildId)) is not null;
        _seasonName = (await points.GetSeasonsAsync(GuildId)).FirstOrDefault(s => s.IsActive)?.Name;

        var detail = await members.GetAsync(GuildId, _targetUserId, historyCount: 1);
        _displayName = detail.DisplayName;
        _avatarUrl = detail.AvatarUrl;

        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _loading = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var points = scope.ServiceProvider.GetRequiredService<PointsReadService>();
            var (from, to) = ResolveRange(_preset);
            var sources = _source is { } s ? new[] { s } : null;
            _activity = await points.GetHistoryPageAsync(GuildId, _targetUserId, _searchBox, _sortKey, _descending, _page, _size, sources: sources, from: from, to: to, sign: _sign);
            _totals = await points.GetHistoryTotalsAsync(GuildId, _targetUserId, _searchBox, sources, from, to, _sign);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task OnRange(ChangeEventArgs e)
    {
        _preset = (e.Value as string) ?? "";
        _page = 1;
        await ReloadAsync();
    }

    private async Task OnSource(ChangeEventArgs e)
    {
        var raw = e.Value as string;
        _source = string.IsNullOrEmpty(raw) || !Enum.TryParse<CurrencyLedgerSource>(raw, out var v) ? null : v;
        _page = 1;
        await ReloadAsync();
    }

    private async Task SetDirection(int? sign)
    {
        if (_sign == sign)
        {
            return;
        }

        _sign = sign;
        _page = 1;
        await ReloadAsync();
    }

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
            _page = 1;
            await ReloadAsync();
            StateHasChanged();
        }
    }

    private async Task SetSort(string column)
    {
        _descending = _sortKey == column ? !_descending : true;
        _sortKey = column;
        _page = 1;
        await ReloadAsync();
    }

    private string Ind(string column) => _sortKey == column ? (_descending ? "▼" : "▲") : "";

    private async Task OnSize(ChangeEventArgs e)
    {
        _size = int.TryParse(e.Value as string, out var v) ? v : 25;
        _page = 1;
        await ReloadAsync();
    }

    private async Task Prev()
    {
        if (_page > 1)
        {
            _page--;
            await ReloadAsync();
        }
    }

    private async Task Next()
    {
        if (_page < TotalPages)
        {
            _page++;
            await ReloadAsync();
        }
    }

    private void ToggleDetail(long id) => _expandedId = _expandedId == id ? null : id;

    private record Line(string? Header, long HeaderNet, MemberLedgerRow Row);

    private IReadOnlyList<Line> Lines()
    {
        if (_activity is null)
        {
            return [];
        }

        var net = Grouped
            ? _activity.Items.GroupBy(r => DateOnly.FromDateTime(r.OccurredAt.UtcDateTime)).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount))
            : null;

        var list = new List<Line>(_activity.Items.Count);
        DateOnly? last = null;
        foreach (var r in _activity.Items)
        {
            if (Grouped)
            {
                var d = DateOnly.FromDateTime(r.OccurredAt.UtcDateTime);
                if (last != d)
                {
                    last = d;
                    list.Add(new Line(DateLabel(d), net![d], r));
                    continue;
                }
            }

            list.Add(new Line(null, 0, r));
        }

        return list;
    }

    private async Task ExportCsvAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var points = scope.ServiceProvider.GetRequiredService<PointsReadService>();
        var (from, to) = ResolveRange(_preset);
        var sources = _source is { } s ? new[] { s } : null;
        var rows = await points.GetHistoryForExportAsync(GuildId, _targetUserId, _searchBox, sources, from, to, _sign);

        var sb = new StringBuilder();
        sb.AppendLine("When (UTC),Amount,Source,Reason");
        foreach (var r in rows)
        {
            sb.Append(r.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Amount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(SourceLabel(r.Source))).Append(',').AppendLine(Csv(r.Reason));
        }

        await JS.InvokeVoidAsync("musterDownload", $"points-{DateTimeOffset.UtcNow.UtcDateTime:yyyyMMdd}.csv", sb.ToString(), "text/csv;charset=utf-8");
    }

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static string DateLabel(DateOnly d)
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        if (d == today)
        {
            return "Today";
        }

        return d == today.AddDays(-1) ? "Yesterday" : d.ToString("ddd, d MMM");
    }

    public void Dispose()
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
    }
}
