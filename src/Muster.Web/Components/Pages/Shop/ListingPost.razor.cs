using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Muster.Contracts;
using Muster.Domain.Entities.Shops;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Commands.Shops;
using Muster.Infrastructure.Services.Shops;
using Muster.Web.Components.Shared;

namespace Muster.Web.Components.Pages.Shop;

public partial class ListingPost
{
    [Parameter] public Guid StoreId { get; set; }

    private string? _name, _description;
    private string _currency = "";
    private string? _categoryId;
    private long _price = 1;
    private int _quantity = 1;
    private bool _acceptsOffers = true;
    private List<string> _tags = [];
    private IReadOnlyList<ShopCurrencyChoice> _currencies = [];
    private IReadOnlyList<ShopCategory> _categories = [];
    private ShopStoreDetail? _store;
    private FeatureVerdict _shopGate;
    private string? _imageKey;
    private bool _imagesAvailable, _uploading, _busy;
    private string? _imageError;

    private const long ReadCeiling = 20 * 1024 * 1024;

    private string CancelHref => $"/guilds/{GuildId}/shop/stores/{StoreId}";

    private PageHeader.Crumb[] Crumbs => _store is null
        ? [new("Shops", $"/guilds/{GuildId}/shop?tab=shops"), new("New listing")]
        : [new("Shops", $"/guilds/{GuildId}/shop?tab=shops"),
           new(_store.Name, $"/guilds/{GuildId}/shop/store/{_store.Slug}"), new("New listing")];

    protected override async Task LoadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        _shopGate = await scope.ServiceProvider.GetRequiredService<Muster.Infrastructure.Services.Platform.IFeatureGate>()
            .EvaluateAsync(GuildId, PlatformFeature.Shop);
        if (!_shopGate.IsEnabled) { return; }

        var reads = scope.ServiceProvider.GetRequiredService<IShopReadService>();
        _currencies = await reads.GetSpendableCurrenciesAsync(GuildId);
        _categories = await reads.GetCategoriesAsync(GuildId);
        _store = await reads.GetStoreAsync(GuildId, StoreId);
        _currency = _currencies.Count > 0 ? _currencies[0].Code : "";
        _imagesAvailable = scope.ServiceProvider.GetService<IShopImageService>() is not NoOpShopImageService and not null;
    }

    private async Task OnImageSelectedAsync(InputFileChangeEventArgs e)
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
            var (result, key) = await images.UploadAsync(ms, ms.Length, e.File.ContentType, ShopImageKind.Listing);
            if (result == ShopImageUploadResult.Ok) { _imageKey = key; }
            else
            {
                _imageError = result switch
                {
                    ShopImageUploadResult.TooLarge => $"Image is too large (max {ImgOpt.Value.MaxImageBytes / (1024d * 1024d):0.#} MB).",
                    ShopImageUploadResult.TooLargeDimensions => $"Image dimensions are too large ({ImgOpt.Value.Describe(ShopImageKind.Listing)}).",
                    ShopImageUploadResult.UnsupportedType => "Unsupported image type.",
                    _ => "Couldn't read that image.",
                };
            }
        }
        catch { _imageError = "Upload failed."; }
        finally { _uploading = false; }
    }

    private async Task SubmitAsync()
    {
        if (_busy || string.IsNullOrWhiteSpace(_name))
        {
            return;
        }

        _busy = true;
        try
        {
            Guid? categoryId = Guid.TryParse(_categoryId, out var cid) ? cid : null;
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            var result = await bus.InvokeAsync<Result<Guid>>(new PostListing(
                GuildId, UserId, StoreId, _name!.Trim(), _currency, _price, _description, categoryId, _quantity,
                ImageKey: _imageKey, ThumbKey: _imageKey, Tags: _tags.Count > 0 ? _tags : null, AcceptsOffers: _acceptsOffers));

            if (result.Ok)
            {
                // Return to the store's management listings (where this new item now appears).
                Nav.NavigateTo($"/guilds/{GuildId}/shop/stores/{StoreId}");
                return;
            }

            Message = ((Result)result).ToCommandResult("").Message;
        }
        finally
        {
            _busy = false;
        }
    }
}
