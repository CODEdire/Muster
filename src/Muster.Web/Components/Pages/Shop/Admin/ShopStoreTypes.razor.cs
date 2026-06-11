using Microsoft.Extensions.DependencyInjection;
using Muster.Contracts;
using Muster.Domain.Entities.Shops;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Commands.Shops;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Shops;

namespace Muster.Web.Components.Pages.Shop.Admin;

public partial class ShopStoreTypes
{
    private FeatureVerdict _shopGate;
    private IReadOnlyList<ShopStoreType> _types = [];
    private string? _newName;
    private string? _newIcon;
    private int _newSort;
    private bool _busy;

    private sealed class TypeEdit { public string Name = ""; public int Sort; public string? Icon; }
    private readonly Dictionary<Guid, TypeEdit> _edit = [];

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
        _types = await scope.ServiceProvider.GetRequiredService<IShopReadService>().GetStoreTypesAsync(GuildId);
        _edit.Clear();
        foreach (var t in _types)
        {
            _edit[t.Id] = new TypeEdit { Name = t.Name, Sort = t.Sort, Icon = t.Icon };
        }
    }

    private async Task AddAsync()
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
            var result = await bus.InvokeAsync<Result<Guid>>(new CreateStoreType(GuildId, UserId, _newName!.Trim(), _newSort, _newIcon));
            Message = ((Result)result).ToCommandResult($"Store type “{_newName!.Trim()}” added.").Message;
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
                .ToCommandResult("Default store types restored.").Message;
        }
        finally { _busy = false; await ReloadAsync(); }
    }

    private async Task SaveAsync(Guid id)
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
            Message = (await bus.InvokeAsync<Result>(new EditStoreType(GuildId, UserId, id, m.Name, m.Sort, m.Icon))).ToCommandResult("Store type updated.").Message;
        }
        finally { _busy = false; await ReloadAsync(); }
    }

    private async Task DeleteAsync(Guid id, string name)
    {
        if (_busy || !await JS.InvokeAsync<bool>("confirm", new object?[] { $"Delete store type “{name}”? Shops of that type lose it." }))
        {
            return;
        }

        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            Message = (await bus.InvokeAsync<Result>(new DeleteStoreType(GuildId, UserId, id))).ToCommandResult($"Store type “{name}” deleted.").Message;
        }
        finally { _busy = false; await ReloadAsync(); }
    }
}
