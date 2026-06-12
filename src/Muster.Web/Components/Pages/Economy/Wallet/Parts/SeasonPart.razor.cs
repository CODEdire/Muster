using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Muster.Domain;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Web;
using Muster.Persistence.Queries;

namespace Muster.Web.Components.Pages.Economy.Wallet.Parts;

public partial class SeasonPart : WalletPart
{
    private bool _hasPoints = true;

    private long _balance;
    private bool _seasonal;
    private string? _seasonName;
    private int _rank;
    private int _holders;
    private long _earned30d;
    private long _lifetime;

    private IReadOnlyList<(SeasonInfo Season, long Total)> _seasonTotals = [];
    private IReadOnlyList<SourceFlow> _earned = [];
    private string _balSpark = "";

    private long SeasonMax => _seasonTotals.Count == 0 ? 1 : Math.Max(1, _seasonTotals.Max(s => s.Total));
    private long SourceMax => _earned.Count == 0 ? 1 : Math.Max(1, _earned.Max(s => s.Earned));

    protected override async Task ReloadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var points = sp.GetRequiredService<PointsReadService>();
        var wallet = sp.GetRequiredService<WalletReadService>();

        var code = CurrencyCodes.PointsCode;
        var to = DateTimeOffset.UtcNow;
        var from = to.AddDays(-30);

        var snap = await points.GetSnapshotAsync(GuildId, TargetUserId);
        _balance = snap.Balance;
        _seasonal = snap.Seasonal;
        _hasPoints = (await points.GetSupplyAsync(GuildId)) is not null;
        _seasonName = (await points.GetSeasonsAsync(GuildId)).FirstOrDefault(s => s.IsActive)?.Name;

        var kpis = await wallet.GetKpisAsync(GuildId, TargetUserId, code, from, to);
        _earned30d = kpis.Earned;
        (_rank, _holders) = await wallet.GetWealthRankAsync(GuildId, TargetUserId, code);
        _earned = (await wallet.GetSourceBreakdownAsync(GuildId, TargetUserId, code, from, to)).Where(s => s.Earned > 0).OrderByDescending(s => s.Earned).Take(5).ToList();
        _balSpark = Spark((await wallet.GetBalanceSeriesAsync(GuildId, TargetUserId, code, from, to)).Select(p => (double)p.Balance));

        _seasonTotals = await points.GetMemberSeasonTotalsAsync(GuildId, TargetUserId);
        _lifetime = _seasonTotals.Sum(s => s.Total);
    }

    private static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Spark(IEnumerable<double> source)
    {
        var vals = source.ToList();
        if (vals.Count < 2)
        {
            return "";
        }

        var min = vals.Min();
        var range = Math.Max(0.001, vals.Max() - min);
        var sb = new StringBuilder();
        for (var i = 0; i < vals.Count; i++)
        {
            var x = i * (100.0 / (vals.Count - 1));
            var y = 24 - (vals[i] - min) / range * 22;
            sb.Append(Fmt(x)).Append(',').Append(Fmt(y)).Append(' ');
        }

        return sb.ToString().TrimEnd();
    }
}
