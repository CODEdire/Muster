using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Muster.Contracts;
using Muster.Domain.Entities.Shops;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Commands.Shops;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Shops;

namespace Muster.Web.Components.Pages.Shop;

public partial class StoreDetail : IDisposable
{
    [Parameter] public Guid StoreId { get; set; }

    private ShopStoreDetail? _store;
    private FeatureVerdict _shopGate;
    private bool _canManage, _imagesAvailable, _isManager, _guildStore;
    private string _tab = "config";

    private string? _name, _description;
    private Guid _storeTypeId;
    private IReadOnlyList<ShopStoreType> _storeTypes = [];
    private string _accent = "", _banner = "", _logo = "";
    private bool _closed;
    private bool _uploading, _logoUploading;
    private string? _imageError, _logoError;
    private ShopManageListingPage? _listingsPage;
    private string? _mSearch;
    private ShopListingStatus? _mStatus;
    private string _mSort = "";
    private int _mPage = 1;
    private const int MPageSize = 20;
    private string MStatusValue => _mStatus?.ToString() ?? "";
    private long _featuredFee;
    private bool _busy;

    protected override async Task LoadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        _shopGate = await scope.ServiceProvider.GetRequiredService<Muster.Infrastructure.Services.Platform.IFeatureGate>()
            .EvaluateAsync(GuildId, PlatformFeature.Shop);
        if (!_shopGate.IsEnabled)
        {
            return; // gated — render the notice, load nothing
        }

        var reads = scope.ServiceProvider.GetRequiredService<IShopReadService>();
        _store = await reads.GetStoreAsync(GuildId, StoreId);
        if (_store is null)
        {
            return;
        }

        var isManager = await Auth.IsShopManagerAsync(GuildId, UserId);
        _isManager = isManager;
        _guildStore = _store.Origin == Muster.Contracts.ShopStoreOrigin.Guild;
        _canManage = isManager || (_store.OwnerId == UserId && await Auth.IsShopCreatorAsync(GuildId, UserId));
        _imagesAvailable = scope.ServiceProvider.GetService<IShopImageService>() is not NoOpShopImageService and not null;

        _name = _store.Name;
        _description = _store.Description;
        _storeTypeId = _store.StoreTypeId ?? Guid.Empty;
        _storeTypes = await reads.GetStoreTypesAsync(GuildId);
        _featuredFee = (await scope.ServiceProvider.GetRequiredService<GuildShopSettingsService>().GetAsync(GuildId)).FeaturedListingFee;
        _accent = _store.AccentColor ?? "";
        _banner = _store.BannerImageKey ?? "";
        _logo = _store.LogoImageKey ?? "";
        _closed = _store.Closed;
        await ReloadListingsAsync();
    }

    private async Task ReloadListingsAsync()
    {
        var (sort, desc) = _mSort switch
        {
            "price-asc" => ("price", false),
            "price-desc" => ("price", true),
            "name" => ("name", false),
            _ => ("created", true),
        };
        await using var scope = Scopes.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IShopReadService>();
        _listingsPage = await reads.GetStoreManageListingsAsync(
            GuildId, StoreId, _mStatus, NullIfBlank(_mSearch), sort, desc, _mPage, MPageSize);
    }

    private async Task ApplyMSearch() { _mPage = 1; await ReloadListingsAsync(); }

    private const int SearchDebounceMs = 350;
    private CancellationTokenSource? _mSearchDebounce;

    private async Task DebouncedMSearchAsync()
    {
        _mSearchDebounce?.Cancel();
        var cts = _mSearchDebounce = new CancellationTokenSource();
        try { await Task.Delay(SearchDebounceMs, cts.Token); }
        catch (TaskCanceledException) { return; }
        if (!cts.IsCancellationRequested) { await ApplyMSearch(); }
    }

    public void Dispose() { _mSearchDebounce?.Cancel(); _mSearchDebounce?.Dispose(); }
    private async Task OnMStatus(ChangeEventArgs e) { _mStatus = Enum.TryParse<ShopListingStatus>(e.Value as string, out var s) ? s : null; _mPage = 1; await ReloadListingsAsync(); }
    private async Task OnMSort(ChangeEventArgs e) { _mSort = e.Value as string ?? ""; _mPage = 1; await ReloadListingsAsync(); }
    private async Task MGo(int page) { _mPage = Math.Max(1, page); await ReloadListingsAsync(); }
    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string SwatchValue(string? accent)
        => !string.IsNullOrWhiteSpace(accent) && accent.StartsWith('#') ? accent : "#7c3aed";

    private void OnAccentPick(ChangeEventArgs e) => _accent = e.Value as string ?? "";

    // Read ceiling for the browser→server stream; the image service enforces the real per-config size cap and
    // returns a friendly "too large" message rather than the stream throwing for typical oversize files.
    private const long ReadCeiling = 20 * 1024 * 1024;

    // Quick-view/preview of one of this store's listings (the shared modal), opened from the listings table.
    private Guid? _previewItem;
    private async Task OnPreviewChangedAsync() { await ReloadListingsAsync(); StateHasChanged(); }

    // Base colour shown behind a transparent banner.
    private static string BannerBg(string? accent)
        => !string.IsNullOrWhiteSpace(accent) && accent.StartsWith('#') ? accent! : "var(--surface)";

    // Storefront hero scrim (matches Storefront.razor) so the edit preview reflects the live treatment.
    private static string BannerScrim(string? accent)
    {
        const string legibility = "linear-gradient(to top, rgba(6,10,16,.86) 2%, rgba(6,10,16,.40) 40%, transparent 66%)";
        return !string.IsNullOrWhiteSpace(accent) && accent.StartsWith('#') && accent.TrimStart('#').Length == 6
            ? $"background:{legibility}, linear-gradient(to top, {accent}b3 0%, {accent}26 38%, transparent 62%)"
            : $"background:{legibility}";
    }

    private string ImageErrorText(ShopImageUploadResult result, ShopImageKind kind) => result switch
    {
        ShopImageUploadResult.TooLarge => $"Image is too large (max {ImgOpt.Value.MaxImageBytes / (1024d * 1024d):0.#} MB).",
        ShopImageUploadResult.TooLargeDimensions => $"Image dimensions are too large ({ImgOpt.Value.Describe(kind)}).",
        ShopImageUploadResult.UnsupportedType => "Unsupported image type.",
        _ => "Couldn't read that image.",
    };

    private async Task OnBannerSelectedAsync(InputFileChangeEventArgs e)
    {
        _imageError = null;
        _uploading = true;
        try
        {
            await using var stream = e.File.OpenReadStream(ReadCeiling);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;

            await using var scope = Scopes.CreateAsyncScope();
            var images = scope.ServiceProvider.GetRequiredService<IShopImageService>();
            var (result, key) = await images.UploadAsync(ms, ms.Length, e.File.ContentType, ShopImageKind.Banner);
            if (result == ShopImageUploadResult.Ok)
            {
                _banner = key!;
            }
            else
            {
                _imageError = ImageErrorText(result, ShopImageKind.Banner);
            }
        }
        catch
        {
            _imageError = "Upload failed.";
        }
        finally
        {
            _uploading = false;
        }
    }

    private async Task OnLogoSelectedAsync(InputFileChangeEventArgs e)
    {
        _logoError = null;
        _logoUploading = true;
        try
        {
            await using var stream = e.File.OpenReadStream(ReadCeiling);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;

            await using var scope = Scopes.CreateAsyncScope();
            var images = scope.ServiceProvider.GetRequiredService<IShopImageService>();
            var (result, key) = await images.UploadAsync(ms, ms.Length, e.File.ContentType, ShopImageKind.Icon);
            if (result == ShopImageUploadResult.Ok)
            {
                _logo = key!;
            }
            else
            {
                _logoError = ImageErrorText(result, ShopImageKind.Icon);
            }
        }
        catch
        {
            _logoError = "Upload failed.";
        }
        finally
        {
            _logoUploading = false;
        }
    }

    private async Task ResyncAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            Message = (await bus.InvokeAsync<Result>(new ResyncShopStore(GuildId, UserId, StoreId))).ToCommandResult("Shop card re-synced.").Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task SaveAsync()
    {
        if (_busy || string.IsNullOrWhiteSpace(_name))
        {
            return;
        }

        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            var result = await bus.InvokeAsync<Result>(new EditStore(
                GuildId, UserId, StoreId, Name: _name!.Trim(), Description: _description ?? "",
                BannerImageKey: _banner, LogoImageKey: _logo, AccentColor: _accent, Closed: _closed, StoreTypeId: _storeTypeId,
                Origin: _isManager ? (_guildStore ? Muster.Contracts.ShopStoreOrigin.Guild : Muster.Contracts.ShopStoreOrigin.Member) : null));
            Message = result.ToCommandResult("Store updated.").Message;
        }
        finally
        {
            _busy = false;
            await LoadAsync();
        }
    }

    private async Task DeleteAsync()
    {
        if (_busy || _store is null
            || !await JS.InvokeAsync<bool>("confirm", new object?[] { $"Delete “{_store.Name}” and all its listings? This can't be undone." }))
        {
            return;
        }

        _busy = true;
        await using var scope = Scopes.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
        var result = await bus.InvokeAsync<Result>(new DeleteStore(GuildId, UserId, StoreId));
        if (result.Ok)
        {
            Nav.NavigateTo($"/guilds/{GuildId}/shop/stores");
            return;
        }

        Message = result.ToCommandResult("").Message;
        _busy = false;
    }

    private async Task FeatureAsync(Guid listingId, string name, string currencyCode)
    {
        // Featuring may burn a flat fee (in the listing's own currency) — confirm only when there's a cost.
        if (_busy || (_featuredFee > 0
            && !await JS.InvokeAsync<bool>("confirm", new object?[] { $"Feature “{name}” for {_featuredFee} {currencyCode}? The fee is burned and isn't refunded." })))
        {
            return;
        }

        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            Message = (await bus.InvokeAsync<Result>(new FeatureListing(GuildId, UserId, listingId))).ToCommandResult($"“{name}” featured.").Message;
        }
        finally { _busy = false; await ReloadListingsAsync(); }
    }

    private async Task UnfeatureAsync(Guid listingId, string name)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            Message = (await bus.InvokeAsync<Result>(new UnfeatureListing(GuildId, UserId, listingId))).ToCommandResult($"“{name}” unfeatured.").Message;
        }
        finally { _busy = false; await ReloadListingsAsync(); }
    }

    private async Task AddStockAsync(Guid listingId, string name)
    {
        if (_busy)
        {
            return;
        }

        var raw = await JS.InvokeAsync<string?>("prompt", new object?[] { $"Add stock to “{name}” — how many units?", "1" });
        if (string.IsNullOrWhiteSpace(raw))
        {
            return; // cancelled
        }

        if (!int.TryParse(raw, out var add) || add < 1)
        {
            Message = "Enter a whole number of units (1 or more).";
            return;
        }

        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            Message = (await bus.InvokeAsync<Result>(new AddListingStock(GuildId, UserId, listingId, add)))
                .ToCommandResult($"Added {add} to “{name}”.").Message;
        }
        finally { _busy = false; await ReloadListingsAsync(); }
    }

    private async Task RelistAsync(Guid listingId, string name)
    {
        if (_busy)
        {
            return;
        }

        var raw = await JS.InvokeAsync<string?>("prompt", new object?[] { $"Relist “{name}” as a new copy — how many units?", "1" });
        if (string.IsNullOrWhiteSpace(raw))
        {
            return; // cancelled
        }

        if (!int.TryParse(raw, out var qty) || qty < 1)
        {
            Message = "Enter a whole number of units (1 or more).";
            return;
        }

        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            var result = await bus.InvokeAsync<Result<Guid>>(new RelistListing(GuildId, UserId, listingId, qty));
            Message = ((Result)result).ToCommandResult($"“{name}” relisted.").Message;
        }
        finally { _busy = false; await ReloadListingsAsync(); }
    }

    private async Task CancelListingAsync(Guid listingId, string name)
    {
        if (_busy || !await JS.InvokeAsync<bool>("confirm", new object?[] { $"Withdraw “{name}”?" }))
        {
            return;
        }

        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            var result = await bus.InvokeAsync<Result>(new CancelListing(GuildId, UserId, listingId));
            Message = result.ToCommandResult($"“{name}” withdrawn.").Message;
        }
        finally
        {
            _busy = false;
            await ReloadListingsAsync();
        }
    }
}
