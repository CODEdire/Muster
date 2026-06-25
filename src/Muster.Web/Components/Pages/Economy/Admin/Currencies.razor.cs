using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Contracts;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Membership;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Wolverine;

namespace Muster.Web.Components.Pages.Economy.Admin;

public partial class Currencies
{
    // EconomyManager + Admin (implicit) — currency lifecycle is economy staff work.
    protected override GuildAccessTier RequiredAccess => GuildAccessTier.EconomyManager;

    private IReadOnlyList<CurrencyView> _currencies = [];

    protected override async Task LoadAsync()
        => _currencies = await CurrencyAdmin.ListAsync(GuildId);

    private static string ConnectorSummary(CurrencyView c)
    {
        var cfg = c.Connector.Config;
        if (c.Mode == CurrencyMode.Internal || !cfg.Enabled)
        {
            return "—";
        }

        var actions = string.Concat(cfg.Credit.Enabled ? "C" : "", cfg.Debit.Enabled ? "D" : "", cfg.GetBalance.Enabled ? "B" : "");
        return $"{cfg.Auth.Scheme} · {(actions.Length > 0 ? actions : "—")}";
    }

    private async Task RebuildAsync()
    {
        // Create any missing wallets (all members × all currencies) + rebuild balances from the ledger, …
        var r = await Currency.RebuildWalletsAsync(GuildId);
        await Audit.RecordCurrencyRebuildAsync(GuildId, UserId, r.BalancesCorrected);

        // … then kick off an external-balance resync for each connector-backed currency (runs in the background).
        var external = _currencies
            .Where(c => c.Mode != Muster.Domain.Enums.CurrencyMode.Internal
                && c.Connector.Config.Enabled && c.Connector.Config.GetBalance.Enabled)
            .ToList();
        foreach (var c in external)
        {
            await Bus.PublishAsync(new SyncCurrencyBalances(GuildId, c.Id));
        }

        var sync = external.Count > 0 ? $" Syncing {external.Count} external currenc(ies) in the background." : "";
        Message = $"Rebuilt for {r.Members} member(s) × {r.Currencies} currenc(ies) — created {r.WalletsCreated} wallet(s), corrected {r.BalancesCorrected} balance(s).{sync}"
            + (r.Members <= 1 ? " ⚠ Only {0} member(s) are known locally — run Members → “Sync from Discord” first to pull the full roster.".Replace("{0}", r.Members.ToString()) : "");
        await LoadAsync();
    }
}
