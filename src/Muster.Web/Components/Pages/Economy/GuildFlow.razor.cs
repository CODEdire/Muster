using Microsoft.Extensions.DependencyInjection;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Web;

namespace Muster.Web.Components.Pages.Economy;

public partial class GuildFlow
{
    // Read-only treasury view — EconomyManager + Auditor. Admin passes implicitly.
    protected override GuildAccessTier RequiredAccess =>
        GuildAccessTier.EconomyManager | GuildAccessTier.Auditor;

    private IReadOnlyList<CurrencyInfo> _currencies = [];
    private string _code = "";
    private bool _loading;

    private FlowView? _flow;

    /// <summary>Largest single source magnitude on either side — for scaling the diverging bars.</summary>
    private long FlowMax
    {
        get
        {
            if (_flow is null)
            {
                return 1;
            }

            long m = 1;
            foreach (var f in _flow.Faucets)
            {
                m = Math.Max(m, f.Total);
            }

            foreach (var s in _flow.Sinks)
            {
                m = Math.Max(m, s.Total);
            }

            return m;
        }
    }

    /// <summary>Inflation needle position (0–100%) across a -10%..+10% band.</summary>
    private int InflationPos => _flow is null ? 50 : (int)Math.Clamp(50 + _flow.InflationPct * 5, 0, 100);

    protected override async Task LoadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var wallet = scope.ServiceProvider.GetRequiredService<WalletReadService>();
        _currencies = await wallet.GetCurrenciesAsync(GuildId);
        if (string.IsNullOrWhiteSpace(_code) || _currencies.All(c => c.Code != _code))
        {
            _code = _currencies.FirstOrDefault(c => c.Primary)?.Code ?? _currencies.FirstOrDefault()?.Code ?? "";
        }

        await ReloadAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (State == AccessState.Ready && _flow is null && !string.IsNullOrWhiteSpace(_code))
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
            var to = DateTimeOffset.UtcNow;
            _flow = await wallet.GetFlowAsync(GuildId, _code, to.AddDays(-30), to);
        }
        finally
        {
            _loading = false;
        }
    }

    private static string InflationRead(double pct) => pct switch
    {
        < 0 => "deflating — burns outpace mints",
        <= 5 => "stable — healthy band",
        _ => "hot — mint outpacing burn",
    };
}
