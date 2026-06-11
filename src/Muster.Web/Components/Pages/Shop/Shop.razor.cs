using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Contracts;
using Muster.Domain.Entities.Shops;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Commands.Shops;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Shops;

namespace Muster.Web.Components.Pages.Shop;

public partial class Shop : IDisposable
{
    // URL state — every view (tab/view/scope/filter/sort/page) lives in the query so views are shareable.
    [SupplyParameterFromQuery] private string? Tab { get; set; }
    [SupplyParameterFromQuery(Name = "lview")] private string? LView { get; set; }
    [SupplyParameterFromQuery(Name = "lcat")] private string? LCat { get; set; }
    [SupplyParameterFromQuery(Name = "ltag")] private string? LTag { get; set; }
    [SupplyParameterFromQuery(Name = "lq")] private string? LQ { get; set; }
    [SupplyParameterFromQuery(Name = "lsort")] private string? LSort { get; set; }
    [SupplyParameterFromQuery(Name = "lpage")] private int LPage { get; set; }
    [SupplyParameterFromQuery(Name = "lmin")] private int? LMin { get; set; }
    [SupplyParameterFromQuery(Name = "lmax")] private int? LMax { get; set; }
    [SupplyParameterFromQuery(Name = "lstock")] private bool LStock { get; set; }
    [SupplyParameterFromQuery(Name = "lcur")] private string? LCur { get; set; }
    [SupplyParameterFromQuery(Name = "sview")] private string? SView { get; set; }
    [SupplyParameterFromQuery(Name = "sscope")] private string? SScope { get; set; }
    [SupplyParameterFromQuery(Name = "sq")] private string? SQ { get; set; }
    [SupplyParameterFromQuery(Name = "ssort")] private string? SSort { get; set; }
    [SupplyParameterFromQuery(Name = "spage")] private int SPage { get; set; }
    [SupplyParameterFromQuery(Name = "stype")] private string? SType { get; set; }
    [SupplyParameterFromQuery(Name = "item")] private string? Item { get; set; }

    private Guid? ItemId => Guid.TryParse(Item, out var g) ? g : null;
    private string CurTab => Tab == "shops" ? "shops" : "listings";
    private string CurLView => LView == "grid" ? "grid" : "tiles";
    private string CurLSort => LSort is "price-asc" or "price-desc" or "name" or "stock" ? LSort : "";
    private int CurLPage => LPage < 1 ? 1 : LPage;
    private string CurSView => SView == "grid" ? "grid" : "tiles";
    private string CurSScope => SScope == "mine" ? "mine" : "all";
    private string CurSSort => SSort is "name" or "name-desc" ? SSort : "";
    private int CurSPage => SPage < 1 ? 1 : SPage;

    private bool _isCreator, _isManager, _ownsStore, _busy;
    private FeatureVerdict _shopGate;
    private IReadOnlyList<ShopCategory> _categories = [];
    private IReadOnlyList<ShopStoreType> _storeTypes = [];
    private ShopBoardPage? _listings;
    private ShopStorePage? _stores;
    private string? _lSearchBox, _sSearchBox, _newStoreName, _newStoreDesc, _newStoreType;
    private bool _newStoreGuild;
    private bool _showCreateStore;
    private string? _lTagBox, _lMinBox, _lMaxBox;
    private IReadOnlyList<ShopCurrencyChoice> _currencies = [];
    private string? _listingsKey, _shopsKey;

    private const int PageSize = 24;

    protected override async Task LoadAsync()
    {
        _isManager = await Auth.IsShopManagerAsync(GuildId, UserId);
        _isCreator = _isManager || await Auth.IsShopCreatorAsync(GuildId, UserId);
        await using var scope = Scopes.CreateAsyncScope();
        _shopGate = await scope.ServiceProvider.GetRequiredService<Muster.Infrastructure.Services.Platform.IFeatureGate>()
            .EvaluateAsync(GuildId, PlatformFeature.Shop);
        if (!_shopGate.IsEnabled)
        {
            return; // gated — skip loading shop data
        }

        var reads = scope.ServiceProvider.GetRequiredService<IShopReadService>();
        _categories = await reads.GetCategoriesAsync(GuildId);
        _storeTypes = await reads.GetStoreTypesAsync(GuildId);
        _ownsStore = (await reads.GetStoresByOwnerAsync(GuildId, UserId)).Count > 0;
        _currencies = await reads.GetSpendableCurrenciesAsync(GuildId);
    }

    // Reload only the tab whose effective query changed (presentational changes like view/tab don't refetch).
    protected override async Task OnParametersSetAsync()
    {
        if (State != AccessState.Ready)
        {
            return;
        }

        _lSearchBox = LQ;
        _sSearchBox = SQ;
        _lTagBox = LTag;
        _lMinBox = LMin?.ToString();
        _lMaxBox = LMax?.ToString();

        var lk = $"{LCat}|{LTag}|{LQ}|{CurLSort}|{CurLPage}|{LMin}|{LMax}|{LStock}|{LCur}";
        if (lk != _listingsKey)
        {
            _listingsKey = lk;
            await ReloadListingsAsync();
        }

        var sk = $"{CurSScope}|{SQ}|{CurSSort}|{CurSPage}|{SType}|{_isManager}";
        if (sk != _shopsKey)
        {
            _shopsKey = sk;
            await ReloadShopsAsync();
        }
    }

    private bool CanManage(ShopStoreRow s) => _isManager || (s.OwnerId == UserId && _isCreator);

    private void OnTab(string tab) => Go(("tab", tab == "listings" ? null : tab));

    private void OnListingCategory(ChangeEventArgs e) => Go(("lcat", e.Value as string is { Length: > 0 } v ? v : null), ("lpage", null));
    private void OnListingSort(ChangeEventArgs e) => Go(("lsort", NullIfEmpty(e.Value as string)), ("lpage", null));
    private void ApplyListingSearch() => Go(("lq", NullIfEmpty(_lSearchBox)), ("lpage", null));
    private void OnListingCurrency(ChangeEventArgs e) => Go(("lcur", e.Value as string is { Length: > 0 } v ? v : null), ("lpage", null));
    private void OnListingStock(ChangeEventArgs e) => Go(("lstock", e.Value is true ? "true" : null), ("lpage", null));

    private CancellationTokenSource? _lTagDebounce, _lPriceDebounce;
    private async Task DebouncedTagAsync()
    {
        _lTagDebounce?.Cancel();
        var cts = _lTagDebounce = new CancellationTokenSource();
        try { await Task.Delay(SearchDebounceMs, cts.Token); } catch (TaskCanceledException) { return; }
        if (!cts.IsCancellationRequested) { Go(("ltag", NullIfEmpty(_lTagBox?.TrimStart('#'))), ("lpage", null)); }
    }
    private async Task DebouncedPriceAsync()
    {
        _lPriceDebounce?.Cancel();
        var cts = _lPriceDebounce = new CancellationTokenSource();
        try { await Task.Delay(SearchDebounceMs, cts.Token); } catch (TaskCanceledException) { return; }
        if (!cts.IsCancellationRequested) { Go(("lmin", ParsePositive(_lMinBox)), ("lmax", ParsePositive(_lMaxBox)), ("lpage", null)); }
    }

    private bool HasListingFilters => !string.IsNullOrEmpty(LCat) || !string.IsNullOrEmpty(LTag) || !string.IsNullOrEmpty(LQ)
        || LMin is not null || LMax is not null || !string.IsNullOrEmpty(LCur) || LStock;
    private void ClearListingFilters() => Go(("lcat", null), ("ltag", null), ("lq", null), ("lmin", null), ("lmax", null), ("lcur", null), ("lstock", null), ("lpage", null));
    private string? CategoryName(string? id) => Guid.TryParse(id, out var g) ? _categories.FirstOrDefault(c => c.Id == g)?.Name : null;
    private string? CurrencyCode(string? id) => Guid.TryParse(id, out var g) ? _currencies.FirstOrDefault(c => c.Id == g)?.Code : null;
    private static string? ParsePositive(string? s) => int.TryParse(s, out var n) && n > 0 ? n.ToString() : null;

    private const int SearchDebounceMs = 350;
    private CancellationTokenSource? _lSearchDebounce, _sSearchDebounce;

    private async Task DebouncedListingSearchAsync()
    {
        _lSearchDebounce?.Cancel();
        var cts = _lSearchDebounce = new CancellationTokenSource();
        try { await Task.Delay(SearchDebounceMs, cts.Token); }
        catch (TaskCanceledException) { return; }
        if (!cts.IsCancellationRequested) { ApplyListingSearch(); }
    }

    private async Task DebouncedShopSearchAsync()
    {
        _sSearchDebounce?.Cancel();
        var cts = _sSearchDebounce = new CancellationTokenSource();
        try { await Task.Delay(SearchDebounceMs, cts.Token); }
        catch (TaskCanceledException) { return; }
        if (!cts.IsCancellationRequested) { ApplyShopSearch(); }
    }

    public void Dispose()
    {
        _lSearchDebounce?.Cancel();
        _lSearchDebounce?.Dispose();
        _sSearchDebounce?.Cancel();
        _sSearchDebounce?.Dispose();
        _lTagDebounce?.Cancel();
        _lTagDebounce?.Dispose();
        _lPriceDebounce?.Cancel();
        _lPriceDebounce?.Dispose();
    }
    private void OnShopSort(ChangeEventArgs e) => Go(("ssort", NullIfEmpty(e.Value as string)), ("spage", null));
    private void OnShopType(ChangeEventArgs e) => Go(("stype", e.Value as string is { Length: > 0 } v ? v : null), ("spage", null));
    private void ApplyShopSearch() => Go(("sq", NullIfEmpty(_sSearchBox)), ("spage", null));

    private async Task ReloadListingsAsync()
    {
        var (sort, desc) = CurLSort switch
        {
            "price-asc" => ("price", false),
            "price-desc" => ("price", true),
            "name" => ("name", false),
            "stock" => ("stock", true),
            _ => ("created", true),
        };
        Guid? cat = Guid.TryParse(LCat, out var c) ? c : null;
        await using var scope = Scopes.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IShopReadService>();
        Guid? cur = Guid.TryParse(LCur, out var cg) ? cg : null;
        _listings = await reads.GetMarketAsync(GuildId, cat, NullIfEmpty(LTag), NullIfEmpty(LQ), sort, desc, CurLPage, PageSize,
            minPrice: LMin, maxPrice: LMax, inStockOnly: LStock, currencyId: cur);
    }

    private async Task ReloadShopsAsync()
    {
        var (sort, desc) = CurSSort switch
        {
            "name" => ("name", false),
            "name-desc" => ("name", true),
            _ => ("created", true),
        };
        ulong? ownerScope = CurSScope == "mine" ? UserId : null;
        var includeClosed = CurSScope == "mine" || _isManager;
        await using var scope = Scopes.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IShopReadService>();
        Guid? stype = Guid.TryParse(SType, out var sg) ? sg : null;
        _stores = await reads.GetStoresAsync(GuildId, ownerScope, includeClosed, NullIfEmpty(SQ), sort, desc, CurSPage, PageSize, storeTypeId: stype);
    }

    private void OpenCreateStore()
    {
        _newStoreName = _newStoreDesc = _newStoreType = null;
        _showCreateStore = true;
    }

    private void CloseCreateStore() => _showCreateStore = false;

    // Quick-create from the popup: "Create" stays on the shops list; "Create & edit" jumps to the full store editor.
    private async Task CreateStoreAsync(bool thenEdit)
    {
        if (_busy || string.IsNullOrWhiteSpace(_newStoreName))
        {
            return;
        }

        Guid? typeId = Guid.TryParse(_newStoreType, out var t) ? t : null;
        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            var desc = string.IsNullOrWhiteSpace(_newStoreDesc) ? null : _newStoreDesc!.Trim();
            var origin = _isManager && _newStoreGuild ? Muster.Contracts.ShopStoreOrigin.Guild : Muster.Contracts.ShopStoreOrigin.Member;
            var result = await bus.InvokeAsync<Result<Guid>>(new CreateStore(GuildId, UserId, _newStoreName!.Trim(), desc, null, typeId, origin));
            if (result.Ok)
            {
                _showCreateStore = false;
                if (thenEdit)
                {
                    Nav.NavigateTo($"/guilds/{GuildId}/shop/stores/{result.Value}");
                    return;
                }
            }
            else
            {
                Message = ((Result)result).ToCommandResult("").Message;
            }
        }
        finally { _busy = false; _shopsKey = null; await ReloadShopsAsync(); }
    }

    private async Task DeleteStoreAsync(Guid storeId, string name)
    {
        if (_busy || !await JS.InvokeAsync<bool>("confirm", new object?[] { $"Delete “{name}” and all its listings? This can't be undone." }))
        {
            return;
        }

        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            Message = (await bus.InvokeAsync<Result>(new DeleteStore(GuildId, UserId, storeId))).ToCommandResult($"Store “{name}” deleted.").Message;
        }
        finally { _busy = false; _shopsKey = null; await ReloadShopsAsync(); }
    }

    // Snapshot the current query, apply the overrides (null = drop the key), and build the shop URL.
    private string BuildUrl(params (string Key, string? Value)[] overrides)
    {
        var q = new Dictionary<string, string?>
        {
            ["tab"] = CurTab == "listings" ? null : CurTab,
            ["lview"] = CurLView == "tiles" ? null : CurLView,
            ["lcat"] = LCat,
            ["ltag"] = NullIfEmpty(LTag),
            ["lq"] = NullIfEmpty(LQ),
            ["lsort"] = NullIfEmpty(CurLSort),
            ["lpage"] = CurLPage == 1 ? null : CurLPage.ToString(),
            ["lmin"] = LMin?.ToString(),
            ["lmax"] = LMax?.ToString(),
            ["lstock"] = LStock ? "true" : null,
            ["lcur"] = NullIfEmpty(LCur),
            ["sview"] = CurSView == "tiles" ? null : CurSView,
            ["sscope"] = CurSScope == "all" ? null : CurSScope,
            ["sq"] = NullIfEmpty(SQ),
            ["ssort"] = NullIfEmpty(CurSSort),
            ["spage"] = CurSPage == 1 ? null : CurSPage.ToString(),
            ["stype"] = NullIfEmpty(SType),
            ["item"] = NullIfEmpty(Item),
        };

        foreach (var (key, value) in overrides)
        {
            q[key] = value;
        }

        var query = string.Join("&", q.Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value!)}"));
        return $"/guilds/{GuildId}/shop{(query.Length > 0 ? "?" + query : "")}";
    }

    // Navigate (no full reload). Opening an item uses a plain link (pushes history so Back closes the modal);
    // filter/tab changes replace, so they don't stack history.
    private void Go(params (string Key, string? Value)[] overrides)
        => Nav.NavigateTo(BuildUrl(overrides), forceLoad: false, replace: true);

    private void CloseItem() => Go(("item", null));

    private async Task OnItemChangedAsync()
    {
        _listingsKey = null;
        await ReloadListingsAsync();
        StateHasChanged();
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
