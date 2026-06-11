using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Contracts;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Shops;

namespace Muster.Web.Components.Pages.Shop.Admin;

public partial class ShopSettings : IDisposable
{
    private GuildShopSettings _s = new();
    private IReadOnlyList<ShopCurrencyChoice> _currencies = [];
    private readonly HashSet<Guid> _allowed = [];
    private IReadOnlyList<Muster.Web.GuildChannelOptions.ChannelOption> _chat = [];

    private enum SaveStatus { None, Saving, Saved, Error }
    private SaveStatus _save = SaveStatus.None;
    private string? _saveMsg;
    private const int DebounceMs = 600;
    private const int HideMs = 8_000;
    private CancellationTokenSource? _debounce, _hide;

    private ulong _originalChannelId;
    private bool _platformBlocked;
    private string? _platformReason;

    // The rest of the config is moot while the shop isn't actually running — lock it when a higher layer blocks it
    // or the guild has the master switch off. The master toggle itself stays editable so it can be turned back on.
    private bool ConfigLocked => _platformBlocked || !_s.PlayerMarketEnabled;

    protected override async Task LoadAsync()
    {
        _s = await SettingsStore.GetAsync(GuildId);
        _originalChannelId = _s.ShopChannelId;
        _allowed.Clear();
        foreach (var id in _s.AllowedCurrencyIds) { _allowed.Add(id); }
        _chat = await Channels.ChatAsync(GuildId);
        await using var scope = Scopes.CreateAsyncScope();
        _currencies = await scope.ServiceProvider.GetRequiredService<IShopReadService>().GetSpendableCurrenciesAsync(GuildId);

        // A platform kill-switch / plan block means the guild toggle can't take effect — grey it out with a reason.
        var verdict = await scope.ServiceProvider.GetRequiredService<IFeatureGate>()
            .EvaluateAsync(GuildId, PlatformFeature.Shop);
        _platformBlocked = !verdict.CanEnable;
        _platformReason = verdict.Reason switch
        {
            FeatureGateReason.NotEntitled => "Not included in this server's plan",
            FeatureGateReason.PlatformDisabled => "Disabled platform-wide",
            _ => null,
        };
    }

    private void ToggleCurrency(Guid id, ChangeEventArgs e)
    {
        if (e.Value is true) { _allowed.Add(id); } else { _allowed.Remove(id); }
        ScheduleSave();
    }

    // Debounced autosave — every change schedules a save ~600ms out, superseding any pending one.
    private void ScheduleSave()
    {
        _debounce?.Cancel();
        _hide?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        _save = SaveStatus.Saving;
        _ = InvokeAsync(async () =>
        {
            try { await Task.Delay(DebounceMs, cts.Token); }
            catch (TaskCanceledException) { return; }
            await SaveAsync();
            StateHasChanged();
            ScheduleHide();
        });
    }

    private void ScheduleHide()
    {
        _hide?.Cancel();
        var hide = _hide = new CancellationTokenSource();
        _ = InvokeAsync(async () =>
        {
            try { await Task.Delay(HideMs, hide.Token); }
            catch (TaskCanceledException) { return; }
            _save = SaveStatus.None;
            StateHasChanged();
        });
    }

    private async Task SaveAsync()
    {
        try
        {
            await SettingsStore.UpsertAsync(GuildId, row =>
            {
                row.PlayerMarketEnabled = _s.PlayerMarketEnabled;
                row.OffersEnabled = _s.OffersEnabled;
                row.TwoStepDelivery = _s.TwoStepDelivery;
                row.RatingsEnabled = _s.RatingsEnabled;
                row.PlayerTagsEnabled = _s.PlayerTagsEnabled;
                row.RequireCategory = _s.RequireCategory;
                row.MaxStoresPerSeller = _s.MaxStoresPerSeller;
                row.MaxActiveListingsPerSeller = _s.MaxActiveListingsPerSeller;
                row.MaxOpenOffersPerBuyer = _s.MaxOpenOffersPerBuyer;
                row.MaxTagsPerListing = _s.MaxTagsPerListing;
                row.MaxFeaturedPerStore = _s.MaxFeaturedPerStore;
                row.CommissionBps = _s.CommissionBps;
                row.FeaturedListingFee = _s.FeaturedListingFee;
                row.MinPrice = _s.MinPrice;
                row.MaxPrice = _s.MaxPrice;
                row.AllowedCurrencyIds = [.. _allowed];
                row.DeliveryConfirmTimeoutHours = _s.DeliveryConfirmTimeoutHours;
                row.UndeliveredTimeoutHours = _s.UndeliveredTimeoutHours;
                row.DisputeTimeoutHours = _s.DisputeTimeoutHours;
                row.OfferExpiryHours = _s.OfferExpiryHours;
                row.ListingDefaultExpiryDays = _s.ListingDefaultExpiryDays;
                row.ListingCooldownMinutes = _s.ListingCooldownMinutes;
                row.RatingWindowHours = _s.RatingWindowHours;
                row.FeaturedDurationHours = _s.FeaturedDurationHours;
                row.ShopChannelId = _s.ShopChannelId;
                row.ShopModChannelId = _s.ShopModChannelId;
            });
            await AuditAsync("shop.settings", "Updated shop settings");

            // Channel just (re)linked or moved → (re)post the featured cards to it.
            if (_s.ShopChannelId != _originalChannelId)
            {
                await ResyncCardsAsync();
                _originalChannelId = _s.ShopChannelId;
            }

            _save = SaveStatus.Saved;
        }
        catch
        {
            _save = SaveStatus.Error;
            _saveMsg = "Couldn't save";
        }
    }

    private async Task ResyncCardsAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>()
            .InvokeAsync<Result>(new ResyncShopChannel(GuildId, UserId));
    }

    private async Task ResyncAsync()
    {
        try
        {
            await ResyncCardsAsync();
            _save = SaveStatus.Saved;
            _saveMsg = "Shop cards re-synced";
        }
        catch
        {
            _save = SaveStatus.Error;
            _saveMsg = "Couldn't re-sync";
        }

        ScheduleHide();
        StateHasChanged();
    }

    public void Dispose()
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        _hide?.Cancel();
        _hide?.Dispose();
    }
}
