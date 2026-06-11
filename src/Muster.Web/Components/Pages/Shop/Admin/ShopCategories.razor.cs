using Microsoft.Extensions.DependencyInjection;
using Muster.Contracts;
using Muster.Domain.Entities.Shops;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Commands.Shops;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Shops;

namespace Muster.Web.Components.Pages.Shop.Admin;

public partial class ShopCategories
{
    private FeatureVerdict _shopGate;
    private IReadOnlyList<ShopCategory> _categories = [];
    private string? _newName;
    private string? _newIcon;
    private int _newSort;
    private bool _busy;

    private sealed class CatEdit { public string Name = ""; public int Sort; public int? Override; public string? Icon; }
    private readonly Dictionary<Guid, CatEdit> _edit = [];

    protected override async Task LoadAsync()
    {
        await using (var scope = Scopes.CreateAsyncScope())
        {
            _shopGate = await scope.ServiceProvider.GetRequiredService<IFeatureGate>()
                .EvaluateAsync(GuildId, PlatformFeature.Shop);
        }
        if (!_shopGate.IsEnabled) { return; }
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        _categories = await scope.ServiceProvider.GetRequiredService<IShopReadService>().GetCategoriesAsync(GuildId);
        _edit.Clear();
        foreach (var c in _categories)
        {
            _edit[c.Id] = new CatEdit { Name = c.Name, Sort = c.Sort, Override = c.CommissionBpsOverride, Icon = c.Icon };
        }
    }

    private async Task AddCategoryAsync()
    {
        if (_busy || string.IsNullOrWhiteSpace(_newName))
        {
            return;
        }

        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            var result = await bus.InvokeAsync<Result<Guid>>(new CreateCategory(GuildId, UserId, _newName!.Trim(), _newSort, null, _newIcon));
            Message = ((Result)result).ToCommandResult($"Category “{_newName!.Trim()}” added.").Message;
            _newName = null;
            _newIcon = null;
            _newSort = 0;
        }
        finally { _busy = false; await ReloadAsync(); }
    }

    private async Task RestoreDefaultsAsync()
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
            Message = (await bus.InvokeAsync<Result>(new SeedGuildDefaults(GuildId, UserId, Restore: true)))
                .ToCommandResult("Default categories restored.").Message;
        }
        finally { _busy = false; await ReloadAsync(); }
    }

    private async Task SaveCategoryAsync(Guid id)
    {
        if (_busy || !_edit.TryGetValue(id, out var m))
        {
            return;
        }

        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            Message = (await bus.InvokeAsync<Result>(new EditCategory(GuildId, UserId, id, m.Name, m.Sort, m.Override, m.Icon))).ToCommandResult("Category updated.").Message;
        }
        finally { _busy = false; await ReloadAsync(); }
    }

    private async Task DeleteCategoryAsync(Guid id, string name)
    {
        if (_busy || !await JS.InvokeAsync<bool>("confirm", new object?[] { $"Delete category “{name}”? Listings in it lose their category." }))
        {
            return;
        }

        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            Message = (await bus.InvokeAsync<Result>(new DeleteCategory(GuildId, UserId, id))).ToCommandResult($"Category “{name}” deleted.").Message;
        }
        finally { _busy = false; await ReloadAsync(); }
    }
}
