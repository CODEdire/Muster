using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Contracts;
using Muster.Domain.Entities.Shops;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Shops;
using Muster.Web.Components.Shared;
using static Muster.Web.Components.Pages.Shop.Core.ShopPresentation;

namespace Muster.Web.Components.Pages.Shop;

public partial class Storefront : IDisposable
{
    [Parameter] public string Slug { get; set; } = string.Empty;
    [SupplyParameterFromQuery(Name = "item")] private string? Item { get; set; }
    [SupplyParameterFromQuery(Name = "cat")] private string? Cat { get; set; }
    [SupplyParameterFromQuery(Name = "sort")] private string? Sort { get; set; }
    [SupplyParameterFromQuery(Name = "q")] private string? Q { get; set; }
    [SupplyParameterFromQuery(Name = "page")] private int Page { get; set; }
    [SupplyParameterFromQuery(Name = "tag")] private string? Tag { get; set; }
    [SupplyParameterFromQuery(Name = "min")] private int? Min { get; set; }
    [SupplyParameterFromQuery(Name = "max")] private int? Max { get; set; }
    [SupplyParameterFromQuery(Name = "stock")] private bool Stock { get; set; }
    [SupplyParameterFromQuery(Name = "cur")] private string? Cur { get; set; }

    private const int PageSize = 24;

    private ShopStorefront? _front;
    private bool _canManage, _isManager;
    private FeatureVerdict _shopGate;
    private ShopBoardPage? _items;
    private IReadOnlyList<ShopCategory> _categories = [];
    private IReadOnlyList<ShopCurrencyChoice> _currencies = [];
    private string? _searchBox, _tagBox, _minBox, _maxBox, _itemsKey;

    private Guid? ItemId => Guid.TryParse(Item, out var g) ? g : null;
    private string CurSort => Sort is "price-asc" or "price-desc" or "name" or "stock" ? Sort : "";
    private int CurPage => Page < 1 ? 1 : Page;

    private string StoreItemUrl(Guid id) => BuildStoreUrl(("item", id.ToString()));
    private void CloseItem() => Go(("item", null));
    private void OnCategory(ChangeEventArgs e) => Go(("cat", e.Value as string is { Length: > 0 } v ? v : null), ("page", null));
    private void OnSort(ChangeEventArgs e) => Go(("sort", NullIfEmpty(e.Value as string)), ("page", null));
    private void OnCurrency(ChangeEventArgs e) => Go(("cur", e.Value as string is { Length: > 0 } v ? v : null), ("page", null));
    private void OnStock(ChangeEventArgs e) => Go(("stock", e.Value is true ? "true" : null), ("page", null));
    private void ApplySearch() => Go(("q", NullIfEmpty(_searchBox)), ("page", null));

    private const int SearchDebounceMs = 350;
    private CancellationTokenSource? _searchDebounce, _tagDebounce, _priceDebounce;

    private async Task DebouncedSearchAsync()
    {
        _searchDebounce?.Cancel();
        var cts = _searchDebounce = new CancellationTokenSource();
        try { await Task.Delay(SearchDebounceMs, cts.Token); }
        catch (TaskCanceledException) { return; }
        if (!cts.IsCancellationRequested) { ApplySearch(); }
    }

    private async Task DebouncedTagAsync()
    {
        _tagDebounce?.Cancel();
        var cts = _tagDebounce = new CancellationTokenSource();
        try { await Task.Delay(SearchDebounceMs, cts.Token); } catch (TaskCanceledException) { return; }
        if (!cts.IsCancellationRequested) { Go(("tag", NullIfEmpty(_tagBox?.TrimStart('#'))), ("page", null)); }
    }

    private async Task DebouncedPriceAsync()
    {
        _priceDebounce?.Cancel();
        var cts = _priceDebounce = new CancellationTokenSource();
        try { await Task.Delay(SearchDebounceMs, cts.Token); } catch (TaskCanceledException) { return; }
        if (!cts.IsCancellationRequested) { Go(("min", ParsePositive(_minBox)), ("max", ParsePositive(_maxBox)), ("page", null)); }
    }

    private bool HasFilters => !string.IsNullOrEmpty(Cat) || !string.IsNullOrEmpty(Tag) || !string.IsNullOrEmpty(Q)
        || Min is not null || Max is not null || !string.IsNullOrEmpty(Cur) || Stock;
    private void ClearFilters() => Go(("cat", null), ("tag", null), ("q", null), ("min", null), ("max", null), ("cur", null), ("stock", null), ("page", null));
    private string? CategoryName(string? id) => Guid.TryParse(id, out var g) ? _categories.FirstOrDefault(c => c.Id == g)?.Name : null;
    private string? CurrencyCode(string? id) => Guid.TryParse(id, out var g) ? _currencies.FirstOrDefault(c => c.Id == g)?.Code : null;
    private static string? ParsePositive(string? s) => int.TryParse(s, out var n) && n > 0 ? n.ToString() : null;

    public void Dispose()
    {
        _searchDebounce?.Cancel(); _searchDebounce?.Dispose();
        _tagDebounce?.Cancel(); _tagDebounce?.Dispose();
        _priceDebounce?.Cancel(); _priceDebounce?.Dispose();
    }
    private async Task OnItemChangedAsync() { await LoadAsync(); await ReloadItemsAsync(); StateHasChanged(); }

    private string BuildStoreUrl(params (string Key, string? Value)[] overrides)
    {
        var q = new Dictionary<string, string?>
        {
            ["cat"] = Cat,
            ["sort"] = NullIfEmpty(CurSort),
            ["q"] = NullIfEmpty(Q),
            ["page"] = CurPage == 1 ? null : CurPage.ToString(),
            ["tag"] = NullIfEmpty(Tag),
            ["min"] = Min?.ToString(),
            ["max"] = Max?.ToString(),
            ["stock"] = Stock ? "true" : null,
            ["cur"] = NullIfEmpty(Cur),
            ["item"] = NullIfEmpty(Item),
        };
        foreach (var (k, v) in overrides) { q[k] = v; }
        var query = string.Join("&", q.Where(kv => !string.IsNullOrEmpty(kv.Value)).Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value!)}"));
        return $"/guilds/{GuildId}/shop/store/{Slug}{(query.Length > 0 ? "?" + query : "")}";
    }

    private void Go(params (string Key, string? Value)[] overrides)
        => Nav.NavigateTo(BuildStoreUrl(overrides), forceLoad: false, replace: true);

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private PageHeader.Crumb[] Crumbs => _front is null ? [] :
    [
        new("Shops", $"/guilds/{GuildId}/shop?tab=shops"),
        new(_front.Name),
    ];

    // Base colour shown behind a transparent banner.
    private static string BannerBg(string? accent)
        => !string.IsNullOrWhiteSpace(accent) && accent.StartsWith('#') ? accent! : "var(--surface)";

    // Bottom scrim behind the overlaid identity. A neutral legibility gradient always applies; when the store has
    // an accent colour it washes up underneath it (per-store tint) without hurting title contrast.
    private static string ScrimStyle(string? accent)
    {
        const string legibility = "linear-gradient(to top, rgba(6,10,16,.86) 2%, rgba(6,10,16,.40) 40%, transparent 66%)";
        return !string.IsNullOrWhiteSpace(accent) && accent.StartsWith('#') && accent.TrimStart('#').Length == 6
            ? $"background:{legibility}, linear-gradient(to top, {accent}b3 0%, {accent}26 38%, transparent 62%)"
            : $"background:{legibility}";
    }

    protected override async Task LoadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        _shopGate = await scope.ServiceProvider.GetRequiredService<IFeatureGate>()
            .EvaluateAsync(GuildId, PlatformFeature.Shop);
        if (!_shopGate.IsEnabled)
        {
            return; // shop off for this guild — render the gated state, skip loading the storefront
        }

        var reads = scope.ServiceProvider.GetRequiredService<IShopReadService>();
        _front = await reads.GetStorefrontAsync(GuildId, Slug);
        if (_front is null)
        {
            return;
        }

        _categories = await reads.GetCategoriesAsync(GuildId);
        _currencies = await reads.GetSpendableCurrenciesAsync(GuildId);

        // Owner (with the creator tier) or a shop manager may add listings / edit the shop (moderation).
        _isManager = await Auth.IsShopManagerAsync(GuildId, UserId);
        _canManage = _isManager || (_front.OwnerId == UserId && await Auth.IsShopCreatorAsync(GuildId, UserId));
    }

    // Items reload when the filter/sort/search/page query changes (keyed); opening the item modal doesn't refetch.
    protected override async Task OnParametersSetAsync()
    {
        if (State != AccessState.Ready || _front is null)
        {
            return;
        }

        _searchBox = Q;
        _tagBox = Tag;
        _minBox = Min?.ToString();
        _maxBox = Max?.ToString();
        var key = $"{Cat}|{CurSort}|{Q}|{CurPage}|{Tag}|{Min}|{Max}|{Stock}|{Cur}";
        if (key != _itemsKey)
        {
            _itemsKey = key;
            await ReloadItemsAsync();
        }
    }

    private async Task ReloadItemsAsync()
    {
        if (_front is null)
        {
            return;
        }

        var (sort, desc) = CurSort switch
        {
            "price-asc" => ("price", false),
            "price-desc" => ("price", true),
            "name" => ("name", false),
            "stock" => ("stock", true),
            _ => ("created", true),
        };
        Guid? cat = Guid.TryParse(Cat, out var c) ? c : null;
        Guid? cur = Guid.TryParse(Cur, out var cg) ? cg : null;
        await using var scope = Scopes.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IShopReadService>();
        _items = await reads.GetMarketAsync(GuildId, cat, NullIfEmpty(Tag), NullIfEmpty(Q), sort, desc, CurPage, PageSize,
            storeId: _front.Id, minPrice: Min, maxPrice: Max, inStockOnly: Stock, currencyId: cur);
    }
}
