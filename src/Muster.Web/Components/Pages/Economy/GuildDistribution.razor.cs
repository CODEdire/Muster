using Microsoft.Extensions.DependencyInjection;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Web;

namespace Muster.Web.Components.Pages.Economy;

public partial class GuildDistribution
{
    // Read-only treasury view — EconomyManager + Auditor. Admin passes implicitly.
    protected override GuildAccessTier RequiredAccess =>
        GuildAccessTier.EconomyManager | GuildAccessTier.Auditor;

    private IReadOnlyList<CurrencyInfo> _currencies = [];
    private string _code = "";
    private bool _loading;

    private DistributionView? _dist;
    private IReadOnlyList<LeaderboardRow> _topHolders = [];

    private int BracketMax => _dist is { Brackets.Count: > 0 } d ? Math.Max(1, d.Brackets.Max(b => b.Count)) : 1;

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
        if (State == AccessState.Ready && _dist is null && !string.IsNullOrWhiteSpace(_code))
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
            _dist = await wallet.GetDistributionAsync(GuildId, _code);
            _topHolders = await wallet.GetTopHoldersLedgerAsync(GuildId, _code, 10);
        }
        finally
        {
            _loading = false;
        }
    }

    private static string Concentration(int top10) => top10 switch
    {
        >= 60 => "high concentration",
        >= 40 => "moderate concentration",
        _ => "fairly spread",
    };
}
