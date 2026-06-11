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

public partial class ListingEdit
{
    [Parameter] public Guid ListingId { get; set; }

    private ShopListingEditView? _view;
    private FeatureVerdict _shopGate;
    private string? _name, _description;
    private List<string> _tags = [];
    private string? _categoryId;
    private long _price;
    private int _quantity = 1;
    private bool _acceptsOffers = true;
    private IReadOnlyList<ShopCategory> _categories = [];
    private ShopStoreDetail? _store;
    private string? _imageKey;
    private bool _imagesAvailable, _uploading, _busy;
    private string? _imageError;

    private const long ReadCeiling = 20 * 1024 * 1024;

    // Editing is reached from the store management screen, so Cancel/Save return there (not the storefront preview).
    private string CancelHref => _store is { } s
        ? $"/guilds/{GuildId}/shop/stores/{s.Id}"
        : $"/guilds/{GuildId}/shop";

    private PageHeader.Crumb[] Crumbs => _store is null
        ? [new("Shops", $"/guilds/{GuildId}/shop?tab=shops"), new("Edit listing")]
        : [new("Shops", $"/guilds/{GuildId}/shop?tab=shops"),
           new(_store.Name, $"/guilds/{GuildId}/shop/store/{_store.Slug}"), new(_name ?? "Listing")];

    protected override async Task LoadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        _shopGate = await scope.ServiceProvider.GetRequiredService<Muster.Infrastructure.Services.Platform.IFeatureGate>()
            .EvaluateAsync(GuildId, PlatformFeature.Shop);
        if (!_shopGate.IsEnabled) { return; }

        var reads = scope.ServiceProvider.GetRequiredService<IShopReadService>();
        _categories = await reads.GetCategoriesAsync(GuildId);
        _view = await reads.GetForEditAsync(GuildId, ListingId);
        _imagesAvailable = scope.ServiceProvider.GetService<IShopImageService>() is not NoOpShopImageService and not null;
        if (_view is not null)
        {
            _name = _view.Name;
            _description = _view.Description;
            _price = _view.Price;
            _quantity = _view.Quantity;
            _acceptsOffers = _view.AcceptsOffers;
            _categoryId = _view.CategoryId?.ToString();
            _tags = _view.Tags.ToList();
            _imageKey = _view.ImageKey;
            _store = await reads.GetStoreAsync(GuildId, _view.StoreId);
        }
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

    private async Task SaveAsync()
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
            var result = await bus.InvokeAsync<Result>(new EditListing(
                GuildId, UserId, ListingId, _name!.Trim(), _description, _price, categoryId, _quantity,
                ImageKey: _imageKey ?? string.Empty, ThumbKey: _imageKey ?? string.Empty, Tags: _tags, AcceptsOffers: _acceptsOffers));

            if (result.Ok)
            {
                Nav.NavigateTo(CancelHref);
                return;
            }

            Message = result.ToCommandResult("").Message;
        }
        finally
        {
            _busy = false;
        }
    }
}
