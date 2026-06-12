using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Tracking;
using Muster.Infrastructure.Services.Web;
using static Muster.Web.Components.Shared.LedgerMeta;

namespace Muster.Web.Components.Pages.Economy;

public partial class GuildLedger : IDisposable
{
    // Read-only ledger view — EconomyManager + Auditor. Admin passes implicitly. Bulk award is admin-only.
    protected override GuildAccessTier RequiredAccess =>
        GuildAccessTier.EconomyManager | GuildAccessTier.Auditor;

    private const int PageSize = 25;
    private const int SearchDebounceMs = 350;

    [SupplyParameterFromQuery(Name = "cur")] private string? Cur { get; set; }

    private IReadOnlyList<CurrencyInfo> _currencies = [];
    private string _code = "";
    private bool _loading;

    private CurrencySupply? _supply;

    // Journal state.
    private PagedResult<JournalRow>? _journal;
    private (long In, long Out) _totals;
    private int _page = 1;
    private string? _searchBox;
    private string _preset = "";
    private CurrencyLedgerSource? _source;
    private int? _sign;
    private string? _account;
    private string _sortKey = "when";
    private bool _descending = true;
    private long? _expandedId;
    private CancellationTokenSource? _searchDebounce;

    private int TotalPages => _journal?.TotalPages ?? 1;
    private bool Grouped => _sortKey == "when";
    private bool Reconciles => _supply is { } s && s.Minted == s.Circulating + s.Escrow + s.Removed;
    private int Pct(long part) => _supply is { Minted: > 0 } s ? (int)(part * 100 / s.Minted) : 0;

    // Donut geometry for the supply-composition ring (r = 48, circumference ≈ 301.593).
    private double Seg(long part) => _supply is { Minted: > 0 } s ? (double)part / s.Minted * 301.593 : 0;
    private string Dash(long part) => $"{Seg(part).ToString("0.##", CultureInfo.InvariantCulture)} 301.593";
    private static string Off(double v) => (-v).ToString("0.##", CultureInfo.InvariantCulture);

    // Bulk award (admin only).
    private bool _isAdmin;
    private IReadOnlyList<BulkRoleOption> _roles = [];
    private string _bulkRoleId = "";
    private long _bulkDelta;
    private string _bulkReason = "";
    private bool _bulkPreviewing;
    private bool _bulkQueuing;
    private bool _bulkConfirm;
    private int _bulkConfirmCount;
    private string? _bulkMessage;
    private CurrencyBulkBatch? _bulkBatch;

    protected override async Task LoadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        _isAdmin = await Auth.IsAdminAsync(GuildId, UserId);
        _currencies = await sp.GetRequiredService<WalletReadService>().GetCurrenciesAsync(GuildId);
        if (!string.IsNullOrWhiteSpace(Cur) && _currencies.Any(c => c.Code == Cur))
        {
            _code = Cur!;
        }
        else if (string.IsNullOrWhiteSpace(_code) || _currencies.All(c => c.Code != _code))
        {
            _code = _currencies.FirstOrDefault(c => c.Primary)?.Code ?? _currencies.FirstOrDefault()?.Code ?? "";
        }

        if (_isAdmin)
        {
            _roles = await sp.GetRequiredService<WebMemberService>().GetRoleOptionsAsync(GuildId);
        }

        await ReloadAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (State == AccessState.Ready && _journal is null && !string.IsNullOrWhiteSpace(_code))
        {
            await ReloadAsync();
        }
    }

    private async Task SelectCurrency(string code)
    {
        if (code == _code)
        {
            return;
        }

        _code = code;
        _page = 1;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (string.IsNullOrWhiteSpace(_code))
        {
            return;
        }

        _loading = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var wallet = scope.ServiceProvider.GetRequiredService<WalletReadService>();
            _supply = await wallet.GetSupplyAsync(GuildId, _code);

            var (from, to) = ResolveRange(_preset);
            var sources = _source is { } s ? new[] { s } : null;
            _journal = await wallet.GetJournalAsync(GuildId, _code, _searchBox, _page, PageSize, sources, from, to, _sign, _account, _sortKey, _descending);
            _totals = await wallet.GetJournalTotalsAsync(GuildId, _code, _searchBox, sources, from, to, _sign, _account);
        }
        finally
        {
            _loading = false;
        }
    }

    // ---- Journal filters ----

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

    private async Task OnAccount(ChangeEventArgs e)
    {
        var raw = e.Value as string;
        _account = string.IsNullOrEmpty(raw) ? null : raw;
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
        }
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

    private record Line(string? Header, long HeaderDebit, long HeaderCredit, JournalRow Row);

    /// <summary>Journal rows for rendering. When date-sorted, a day header (with that day's debit/credit subtotals)
    /// precedes each new day; otherwise a flat list.</summary>
    private IReadOnlyList<Line> Lines()
    {
        if (_journal is null)
        {
            return [];
        }

        var byDay = Grouped
            ? _journal.Items.GroupBy(r => DateOnly.FromDateTime(r.OccurredAt.UtcDateTime))
                .ToDictionary(g => g.Key, g => (Debit: g.Sum(x => x.Amount < 0 ? -x.Amount : 0L), Credit: g.Sum(x => x.Amount > 0 ? x.Amount : 0L)))
            : null;

        var list = new List<Line>(_journal.Items.Count);
        DateOnly? last = null;
        foreach (var r in _journal.Items)
        {
            if (Grouped)
            {
                var d = DateOnly.FromDateTime(r.OccurredAt.UtcDateTime);
                if (last != d)
                {
                    last = d;
                    var t = byDay![d];
                    list.Add(new Line(DateLabel(d), t.Debit, t.Credit, r));
                    continue;
                }
            }

            list.Add(new Line(null, 0, 0, r));
        }

        return list;
    }

    private async Task SetSort(string column)
    {
        _descending = _sortKey == column ? !_descending : true;
        _sortKey = column;
        _page = 1;
        await ReloadAsync();
    }

    private string Ind(string column) => _sortKey == column ? (_descending ? "▼" : "▲") : "";

    private void ToggleDetail(long id) => _expandedId = _expandedId == id ? null : id;

    private async Task ExportCsvAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var wallet = scope.ServiceProvider.GetRequiredService<WalletReadService>();
        var (from, to) = ResolveRange(_preset);
        var sources = _source is { } s ? new[] { s } : null;
        var rows = await wallet.GetJournalForExportAsync(GuildId, _code, _searchBox, sources, from, to, _sign, _account);

        var sb = new StringBuilder();
        sb.AppendLine("When (UTC),Account,Counter-account,Source,Debit,Credit,Reference,Reason");
        foreach (var r in rows)
        {
            sb.Append(r.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(r.AccountName)).Append(',').Append(Csv(r.CounterName)).Append(',').Append(Csv(SourceLabel(r.Source))).Append(',')
              .Append(r.Amount < 0 ? (-r.Amount).ToString(CultureInfo.InvariantCulture) : "").Append(',')
              .Append(r.Amount > 0 ? r.Amount.ToString(CultureInfo.InvariantCulture) : "").Append(',')
              .Append(Csv(r.Reference ?? "")).Append(',').AppendLine(Csv(r.Reason));
        }

        await JS.InvokeVoidAsync("musterDownload", $"ledger-{_code}-{DateTimeOffset.UtcNow.UtcDateTime:yyyyMMdd}.csv", sb.ToString(), "text/csv;charset=utf-8");
    }

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static string DateLabel(DateOnly d)
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        if (d == today)
        {
            return "Today";
        }

        return d == today.AddDays(-1) ? "Yesterday" : d.ToString("ddd, d MMM yyyy");
    }

    // ---- Bulk handlers ----

    private void OnBulkRole(ChangeEventArgs e)
    {
        _bulkRoleId = (e.Value as string) ?? "";
        _bulkConfirm = false;
        _bulkMessage = null;
    }

    private void OnBulkDelta(ChangeEventArgs e)
    {
        _bulkDelta = long.TryParse(e.Value as string, out var v) ? v : 0;
        _bulkConfirm = false;
        _bulkMessage = null;
    }

    private void OnBulkReason(ChangeEventArgs e)
    {
        _bulkReason = (e.Value as string) ?? "";
        _bulkConfirm = false;
        _bulkMessage = null;
    }

    private async Task PreviewBulkAsync()
    {
        _bulkMessage = null;
        if (_bulkDelta == 0) { _bulkMessage = "Amount must be non-zero."; return; }
        if (string.IsNullOrWhiteSpace(_bulkReason)) { _bulkMessage = "A reason is required."; return; }
        if (!ulong.TryParse(_bulkRoleId, out var roleId)) { _bulkMessage = "Pick a role."; return; }

        _bulkPreviewing = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var members = scope.ServiceProvider.GetRequiredService<WebMemberService>();
            var targets = await members.GetRoleTargetsAsync(GuildId, roleId);
            if (targets.Count == 0)
            {
                _bulkMessage = "That role has no members.";
                _bulkConfirm = false;
                return;
            }

            _bulkConfirmCount = targets.Count;
            _bulkConfirm = true;
        }
        finally
        {
            _bulkPreviewing = false;
        }
    }

    private async Task QueueBulkAsync()
    {
        if (!ulong.TryParse(_bulkRoleId, out var roleId))
        {
            _bulkMessage = "Pick a role.";
            return;
        }

        _bulkQueuing = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var members = sp.GetRequiredService<WebMemberService>();
            var bulk = sp.GetRequiredService<ICurrencyBulkService>();

            var targets = await members.GetRoleTargetsAsync(GuildId, roleId);
            var result = await bulk.QueueAsync(GuildId, UserId, _code, _bulkDelta, _bulkReason, targets);
            _bulkMessage = result.Message;
            _bulkConfirm = false;

            if (result.Ok && result.BatchId is { } batchId)
            {
                await sp.GetRequiredService<AuditService>()
                    .RecordBulkQueueAsync(GuildId, UserId, _code, _bulkDelta, targets.Count, _bulkReason, batchId);
                _bulkBatch = await bulk.GetAsync(GuildId, batchId);
                _bulkDelta = 0;
                _bulkReason = "";
                await ReloadAsync();
            }
        }
        finally
        {
            _bulkQueuing = false;
        }
    }

    private async Task RefreshBatchAsync()
    {
        if (_bulkBatch is null) return;
        await using var scope = Scopes.CreateAsyncScope();
        _bulkBatch = await scope.ServiceProvider.GetRequiredService<ICurrencyBulkService>().GetAsync(GuildId, _bulkBatch.Id);
    }

    public void Dispose()
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
    }
}
