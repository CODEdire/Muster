using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Domain;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Web;
using Muster.Web.Components.Shared;

namespace Muster.Web.Components.Pages.Economy;

public partial class GuildWallet
{
    [Parameter] public string? Section { get; set; }
    [SupplyParameterFromQuery(Name = "cur")] private string? Cur { get; set; }
    [SupplyParameterFromQuery(Name = "member")] private string? MemberRaw { get; set; }

    // Target member: economy managers (admin implied) view + adjust anyone; auditors view read-only; others self.
    private ulong _targetUserId;
    private bool _viewingOther;
    private bool _canManage;
    private bool _ledgerOnly;   // auditor viewing another member — restrict to ledger tabs, hide mint/transfer

    private string _displayName = "";
    private string? _avatarUrl;

    // Account shows spendable wallet currencies only; points live in the dedicated Season + Points tabs.
    private IReadOnlyList<CurrencyInfo> _walletCurrencies = [];
    private bool _hasPoints;
    private string _sel = "";

    private string _section = "account";

    protected override async Task LoadAsync()
    {
        _canManage = await Auth.IsEconomyManagerAsync(GuildId, UserId);
        var canViewOthers = _canManage || await Auth.IsAuditorAsync(GuildId, UserId);
        _targetUserId = ulong.TryParse(MemberRaw, out var m) && canViewOthers ? m : UserId;
        _viewingOther = _targetUserId != UserId;
        _ledgerOnly = _viewingOther && !_canManage;

        await using var scope = Scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var currencies = await sp.GetRequiredService<ICurrencyReadService>().GetCurrenciesAsync(GuildId);
        _hasPoints = currencies.Any(c => string.Equals(c.Code, CurrencyCodes.PointsCode, StringComparison.OrdinalIgnoreCase));
        _walletCurrencies = currencies.Where(c => !string.Equals(c.Code, CurrencyCodes.PointsCode, StringComparison.OrdinalIgnoreCase)).ToList();
        ResolveSel();

        var detail = await sp.GetRequiredService<WebMemberService>().GetAsync(GuildId, _targetUserId, historyCount: 1);
        _displayName = detail.DisplayName;
        _avatarUrl = detail.AvatarUrl;

        _section = EffectiveSection(Section);
    }

    protected override void OnParametersSet()
    {
        if (State != AccessState.Ready)
        {
            return;
        }

        _section = EffectiveSection(Section);
        if (!string.IsNullOrWhiteSpace(Cur) && Cur != _sel && _walletCurrencies.Any(c => c.Code == Cur))
        {
            _sel = Cur!;
        }
    }

    private void ResolveSel()
    {
        if (!string.IsNullOrWhiteSpace(Cur) && _walletCurrencies.Any(c => c.Code == Cur))
        {
            _sel = Cur!;
        }
        else if (string.IsNullOrWhiteSpace(_sel) || _walletCurrencies.All(c => c.Code != _sel))
        {
            _sel = _walletCurrencies.FirstOrDefault(c => c.Primary)?.Code
                ?? _walletCurrencies.FirstOrDefault(c => c.Spendable)?.Code
                ?? _walletCurrencies.FirstOrDefault()?.Code
                ?? "";
        }
    }

    /// <summary>Resolve the requested tab; auditors viewing another member are pinned to the ledger tabs.</summary>
    private string EffectiveSection(string? s)
    {
        var n = s?.ToLowerInvariant() switch
        {
            "analytics" => "analytics",
            "season" => "season",
            "points" => "points",
            _ => "account",
        };
        return _ledgerOnly ? (n == "points" ? "points" : "account") : n;
    }

    private void SelectCurrency(string code) => _sel = code;

    private string? MemberQ => _viewingOther ? _targetUserId.ToString() : null;

    private IReadOnlyList<TabBar.TabItem> TabItems()
    {
        string Q(bool withCur)
        {
            var parts = new List<string>();
            if (withCur && !string.IsNullOrWhiteSpace(_sel)) parts.Add($"cur={Uri.EscapeDataString(_sel)}");
            if (!string.IsNullOrWhiteSpace(MemberQ)) parts.Add($"member={MemberQ}");
            return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
        }

        var items = new List<TabBar.TabItem>
        {
            new("account", "Account", "payments", $"/guilds/{GuildId}/wallet{Q(true)}"),
        };
        if (!_ledgerOnly)
        {
            items.Add(new("analytics", "Analytics", "monitoring", $"/guilds/{GuildId}/wallet/analytics{Q(true)}"));
            if (_hasPoints)
            {
                items.Add(new("season", "Season", "local_fire_department", $"/guilds/{GuildId}/wallet/season{Q(false)}"));
            }
        }
        if (_hasPoints)
        {
            items.Add(new("points", "Points", "trophy", $"/guilds/{GuildId}/wallet/points{Q(false)}"));
        }
        return items;
    }
}
