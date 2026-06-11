using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Contracts;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Commands.Shops;
using Muster.Infrastructure.Services.Shops;

namespace Muster.Web.Components.Pages.Shop;

public partial class MyOrders
{
    [SupplyParameterFromQuery(Name = "tab")] private string? Tab { get; set; }
    [SupplyParameterFromQuery(Name = "store")] private string? Store { get; set; }

    private Guid? StoreId => Guid.TryParse(Store, out var g) ? g : null;

    private IReadOnlyList<ShopOrderRow> _purchases = [];
    private IReadOnlyList<ShopOrderRow> _sales = [];
    private IReadOnlyList<ShopOrderRow> _disputes = [];
    private List<ShopOrderRow> _offers = [];
    private bool _isManager;
    private FeatureVerdict _shopGate;
    private string _activeTab = "purchases";
    private string? _filterStoreName, _filterStoreSlug;
    private string? _key;
    private bool _busy;
    private string _zoneId = Muster.Infrastructure.Services.Platform.TimeZoneService.Utc;

    // Shared client-side filter/sort/paging state (per-member lists are small, so we page in-memory).
    private const int PageSize = 10;
    private string? _q, _minBox, _maxBox;
    private string _statusFilter = "", _sort = "", _fCategory = "", _fStore = "", _fPerson = "";
    private DateOnly? _from, _to;
    private int _page = 1;

    private void ResetPage() => _page = 1;

    private bool HasFilters => !string.IsNullOrWhiteSpace(_q) || _statusFilter.Length > 0 || _fCategory.Length > 0
        || _fStore.Length > 0 || _fPerson.Length > 0 || !string.IsNullOrWhiteSpace(_minBox) || !string.IsNullOrWhiteSpace(_maxBox)
        || _from is not null || _to is not null;

    private void ClearFilters()
    {
        _q = _minBox = _maxBox = null;
        _from = _to = null;
        _statusFilter = _fCategory = _fStore = _fPerson = "";
        _page = 1;
    }

    private static IEnumerable<string> DistinctCategories(IReadOnlyList<ShopOrderRow> o)
        => o.Where(x => x.CategoryName is { Length: > 0 }).Select(x => x.CategoryName!).Distinct().OrderBy(x => x);
    private static IEnumerable<string> DistinctStores(IReadOnlyList<ShopOrderRow> o)
        => o.Where(x => x.StoreName is { Length: > 0 }).Select(x => x.StoreName!).Distinct().OrderBy(x => x);
    private static IEnumerable<string> DistinctPeople(IReadOnlyList<ShopOrderRow> o, bool buyerView)
        => o.Select(x => buyerView ? x.SellerName : x.BuyerName).Where(n => !string.IsNullOrEmpty(n)).Distinct().OrderBy(x => x);
    private static IEnumerable<string> DistinctBothParties(IReadOnlyList<ShopOrderRow> o)
        => o.SelectMany(x => new[] { x.BuyerName, x.SellerName }).Where(n => !string.IsNullOrEmpty(n)).Distinct().OrderBy(x => x);

    // Data + tab seed live here (keyed on tab/store) so the page reloads when the shop filter or deep-linked
    // tab changes; tab clicks afterwards are client-side via @bind-ActiveId.
    protected override async Task OnParametersSetAsync()
    {
        if (State != AccessState.Ready)
        {
            return;
        }

        var key = $"{Tab}|{Store}";
        if (key != _key)
        {
            _key = key;
            _page = 1;
            _zoneId = await TimeZones.ResolveZoneIdAsync(GuildId, UserId);
            await ReloadAsync();
            // Disputes is manager-only; fall back to purchases if a non-manager deep-links to it.
            _activeTab = Tab switch
            {
                "sales" => "sales",
                "offers" => "offers",
                "disputes" when _isManager => "disputes",
                _ => "purchases",
            };
        }
    }

    private async Task ReloadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        _shopGate = await scope.ServiceProvider.GetRequiredService<Muster.Infrastructure.Services.Platform.IFeatureGate>()
            .EvaluateAsync(GuildId, PlatformFeature.Shop);
        if (!_shopGate.CanEnable)
        {
            _purchases = _sales = _disputes = []; _offers = [];
            return; // platform/plan block — render the gated state, load nothing
        }

        var reads = scope.ServiceProvider.GetRequiredService<IShopReadService>();
        _isManager = await Auth.IsShopManagerAsync(GuildId, UserId);
        _purchases = await reads.GetPurchasesAsync(GuildId, UserId, StoreId);
        _sales = await reads.GetSalesAsync(GuildId, UserId, StoreId);
        _disputes = _isManager ? await reads.GetDisputesAsync(GuildId, StoreId) : [];
        // Offers I'm party to (made as a buyer or received as a seller), consolidated for the Offers tab.
        _offers = _purchases.Concat(_sales).Where(o => o.Status == ShopOrderStatus.OfferPending).DistinctBy(o => o.Id).ToList();
        if (StoreId is { } sid)
        {
            var store = await reads.GetStoreAsync(GuildId, sid);
            _filterStoreName = store?.Name;
            _filterStoreSlug = store?.Slug;
        }
        else { _filterStoreName = _filterStoreSlug = null; }
    }

    private List<ShopOrderRow> Filtered(IReadOnlyList<ShopOrderRow> orders, bool buyerView, bool disputeView = false)
    {
        IEnumerable<ShopOrderRow> q = orders;
        if (!string.IsNullOrWhiteSpace(_q))
        {
            var term = _q.Trim();
            q = q.Where(o => o.ItemName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || o.BuyerName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || o.SellerName.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        if (!disputeView && _statusFilter.Length > 0 && Enum.TryParse<ShopOrderStatus>(_statusFilter, out var st))
        {
            q = q.Where(o => o.Status == st);
        }

        if (_fCategory.Length > 0) { q = q.Where(o => o.CategoryName == _fCategory); }
        if (_fStore.Length > 0) { q = q.Where(o => o.StoreName == _fStore); }
        if (_fPerson.Length > 0)
        {
            q = disputeView
                ? q.Where(o => o.BuyerName == _fPerson || o.SellerName == _fPerson)
                : q.Where(o => (buyerView ? o.SellerName : o.BuyerName) == _fPerson);
        }
        if (long.TryParse(_minBox, out var min)) { q = q.Where(o => o.Amount >= min); }
        if (long.TryParse(_maxBox, out var max)) { q = q.Where(o => o.Amount <= max); }
        if (_from is { } from) { q = q.Where(o => DateOnly.FromDateTime(o.CreatedAt.UtcDateTime) >= from); }
        if (_to is { } to) { q = q.Where(o => DateOnly.FromDateTime(o.CreatedAt.UtcDateTime) <= to); }

        q = _sort switch
        {
            "old" => q.OrderBy(o => o.CreatedAt),
            "amount-desc" => q.OrderByDescending(o => o.Amount),
            "amount-asc" => q.OrderBy(o => o.Amount),
            "name" => q.OrderBy(o => o.ItemName),
            _ => q.OrderByDescending(o => o.CreatedAt),
        };
        return q.ToList();
    }

    private int TotalPages(List<ShopOrderRow> filtered) => Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)PageSize));
    private int ClampedPage(List<ShopOrderRow> filtered) => Math.Min(_page, TotalPages(filtered));
    private List<ShopOrderRow> PageRows(List<ShopOrderRow> filtered) => filtered.Skip((ClampedPage(filtered) - 1) * PageSize).Take(PageSize).ToList();

    private void PrevPage() { if (_page > 1) { _page--; } }
    private void NextPage() => _page++;

    private static bool IsPending(ShopOrderStatus s) => s is ShopOrderStatus.PendingDelivery or ShopOrderStatus.Delivered;

    private static string StatusDot(ShopOrderStatus s) => s switch
    {
        ShopOrderStatus.Settled => "is-active",
        ShopOrderStatus.Refunded or ShopOrderStatus.Cancelled => "is-ended",
        ShopOrderStatus.Disputed => "is-ended",
        _ => "",
    };

    private async Task ConfirmAsync(Guid orderId)
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
            Message = (await bus.InvokeAsync<Result>(new ConfirmReceipt(GuildId, UserId, orderId))).ToCommandResult("Receipt confirmed — funds released to the seller.").Message;
        }
        finally { _busy = false; await ReloadAsync(); }
    }

    private async Task CancelAsync(Guid orderId, string item)
    {
        if (_busy || !await JS.InvokeAsync<bool>("confirm", new object?[] { $"Cancel the order for “{item}” and refund the buyer?" }))
        {
            return;
        }

        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            Message = (await bus.InvokeAsync<Result>(new SellerCancelOrder(GuildId, UserId, orderId))).ToCommandResult("Order cancelled — buyer refunded.").Message;
        }
        finally { _busy = false; await ReloadAsync(); }
    }

    // Whose turn it is in a negotiation: the seller responds to a buyer-proposed price; the buyer responds to a counter.
    private static bool YourTurn(ShopOrderRow o, bool buyerView)
        => buyerView ? o.OfferProposedBy == ShopOfferParty.Seller : o.OfferProposedBy == ShopOfferParty.Buyer;
}
