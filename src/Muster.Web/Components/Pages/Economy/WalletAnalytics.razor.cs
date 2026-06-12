using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Domain;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Web;
using static Muster.Web.Components.Shared.LedgerMeta;

namespace Muster.Web.Components.Pages.Economy;

public partial class WalletAnalytics
{
    private static readonly string[] MonthNames =
        ["", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    private IReadOnlyList<CurrencyInfo> _currencies = [];
    private string _sel = "";
    private string _displayName = "";
    private string _period = "6mo";

    private CurrencyInfo? Selected => _currencies.FirstOrDefault(c => c.Code == _sel);
    private string SelUnit => Selected?.Code ?? "";

    private WalletKpis _kpis = new(0, 0, 0, 0, 0, 0);
    private int _heldOrders;
    private int _rank;
    private int _holders;
    private IReadOnlyList<BalancePoint> _series = [];
    private IReadOnlyList<MonthFlow> _months = [];
    private IReadOnlyList<SourceFlow> _earned = [];
    private IReadOnlyList<SourceFlow> _spent = [];

    private long EarnedMax => _earned.Count == 0 ? 1 : Math.Max(1, _earned.Max(s => s.Earned));
    private long SpentMax => _spent.Count == 0 ? 1 : Math.Max(1, _spent.Max(s => s.Spent));
    private long FlowMax => _months.Count == 0 ? 1 : Math.Max(1, _months.Max(m => Math.Max(m.Earned, m.Spent)));
    private int NetPositiveMonths => _months.Count(m => m.Earned - m.Spent >= 0);

    private static readonly (string Value, string Label)[] Periods =
        [("30d", "Last 30 days"), ("90d", "Last 90 days"), ("6mo", "Last 6 months"), ("1y", "Last year")];

    protected override async Task LoadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        _currencies = await sp.GetRequiredService<ICurrencyReadService>().GetCurrenciesAsync(GuildId);
        _displayName = (await sp.GetRequiredService<WebMemberService>().GetAsync(GuildId, UserId, historyCount: 1)).DisplayName;
        _sel = _currencies.FirstOrDefault(c => c.Primary)?.Code
            ?? _currencies.FirstOrDefault(c => c.Spendable)?.Code
            ?? _currencies.FirstOrDefault()?.Code
            ?? "";

        await LoadDataAsync();
    }

    private (DateTimeOffset From, DateTimeOffset To) Window()
    {
        var to = DateTimeOffset.UtcNow;
        var from = _period switch
        {
            "30d" => to.AddDays(-30),
            "90d" => to.AddDays(-90),
            "1y" => to.AddDays(-365),
            _ => to.AddDays(-180),
        };
        return (from, to);
    }

    private async Task LoadDataAsync()
    {
        if (string.IsNullOrEmpty(_sel))
        {
            return;
        }

        await using var scope = Scopes.CreateAsyncScope();
        var wallet = scope.ServiceProvider.GetRequiredService<WalletReadService>();
        var (from, to) = Window();

        _kpis = await wallet.GetKpisAsync(GuildId, UserId, _sel, from, to);
        _heldOrders = _kpis.Held > 0 ? await wallet.GetHeldOrderCountAsync(GuildId, UserId, _sel) : 0;
        (_rank, _holders) = await wallet.GetWealthRankAsync(GuildId, UserId, _sel);
        _series = await wallet.GetBalanceSeriesAsync(GuildId, UserId, _sel, from, to);
        _months = await wallet.GetCashFlowAsync(GuildId, UserId, _sel, from, to);

        var breakdown = await wallet.GetSourceBreakdownAsync(GuildId, UserId, _sel, from, to);
        _earned = breakdown.Where(s => s.Earned > 0).OrderByDescending(s => s.Earned).ToList();
        _spent = breakdown.Where(s => s.Spent > 0).OrderByDescending(s => s.Spent).ToList();
    }

    private async Task SelectCurrency(string code)
    {
        if (code == _sel)
        {
            return;
        }

        _sel = code;
        await LoadDataAsync();
    }

    private async Task OnPeriod(ChangeEventArgs e)
    {
        _period = (e.Value as string) ?? "6mo";
        await LoadDataAsync();
    }

    private string Initials()
    {
        var parts = _displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "?";
        }

        var s = parts[0][..1];
        if (parts.Length > 1)
        {
            s += parts[^1][..1];
        }

        return s.ToUpperInvariant();
    }

    private static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Balance-over-time polyline points over a 600×150 viewbox (empty when too few points).</summary>
    private string BalanceLine()
    {
        if (_series.Count < 2)
        {
            return "";
        }

        long min = _series.Min(p => p.Balance);
        long max = _series.Max(p => p.Balance);
        double range = max - min == 0 ? 1 : max - min;
        int n = _series.Count;

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n; i++)
        {
            double x = i * (600.0 / (n - 1));
            double y = 140 - (_series[i].Balance - min) / range * 130;
            sb.Append(Fmt(x)).Append(',').Append(Fmt(y)).Append(' ');
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Closed area path matching <see cref="BalanceLine"/> (line + baseline) for the soft fill.</summary>
    private string BalanceArea()
    {
        var line = BalanceLine();
        return string.IsNullOrEmpty(line) ? "" : $"M0,150 L{line.Replace(" ", " L")} L600,150 Z";
    }

    private string MonthLabel(MonthFlow m) => MonthNames[m.Month];
    private double BarHeight(long value) => value / (double)FlowMax * 120;
    private string PercentOfEarned(SourceFlow s) => _earned.Sum(x => x.Earned) is var t && t > 0 ? $"{s.Earned * 100 / t}%" : "0%";
}
