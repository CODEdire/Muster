using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Web;

namespace Muster.Web.Components.Pages.Economy;

public partial class GuildTreasury
{
    // Read-only treasury shell — EconomyManager + Auditor. Admin passes implicitly. Hosts the header, currency
    // switcher, mint/adjust and tab bar; each tab body is an embedded part that loads its own data.
    protected override GuildAccessTier RequiredAccess =>
        GuildAccessTier.EconomyManager | GuildAccessTier.Auditor;

    [Parameter] public string? Tab { get; set; }
    [SupplyParameterFromQuery(Name = "cur")] private string? Cur { get; set; }

    private string _tab = "overview";
    private IReadOnlyList<CurrencyInfo> _currencies = [];
    private string _code = "";
    private bool _isAdmin;
    private bool _mintOpen;

    // Bumped after a mint/adjust to force the active part to reload without changing currency.
    private int _revision;

    protected override async Task LoadAsync()
    {
        _isAdmin = await Auth.IsAdminAsync(GuildId, UserId);
        await using var scope = Scopes.CreateAsyncScope();
        _currencies = await scope.ServiceProvider.GetRequiredService<WalletReadService>().GetCurrenciesAsync(GuildId);
        ResolveCode();
        _tab = Normalize(Tab);
    }

    protected override void OnParametersSet()
    {
        if (State != AccessState.Ready)
        {
            return;
        }

        _tab = Normalize(Tab);
        if (!string.IsNullOrWhiteSpace(Cur) && Cur != _code && _currencies.Any(c => c.Code == Cur))
        {
            _code = Cur!;
        }
    }

    private void ResolveCode()
    {
        if (!string.IsNullOrWhiteSpace(Cur) && _currencies.Any(c => c.Code == Cur))
        {
            _code = Cur!;
        }
        else if (string.IsNullOrWhiteSpace(_code) || _currencies.All(c => c.Code != _code))
        {
            _code = _currencies.FirstOrDefault(c => c.Primary)?.Code ?? _currencies.FirstOrDefault()?.Code ?? "";
        }
    }

    private static string Normalize(string? tab) => tab?.ToLowerInvariant() switch
    {
        "ledger" => "ledger",
        "distribution" => "distribution",
        "flow" => "flow",
        _ => "overview",
    };

    private void SelectCurrency(string code) => _code = code;

    private void OnMintApplied()
    {
        _mintOpen = false;
        _revision++;
    }
}
