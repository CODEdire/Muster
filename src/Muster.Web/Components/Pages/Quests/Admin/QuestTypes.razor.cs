using Microsoft.Extensions.DependencyInjection;
using Muster.Contracts;
using Muster.Domain.Entities.Quests;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Commands.Quests;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Quests;

namespace Muster.Web.Components.Pages.Quests.Admin;

public partial class QuestTypes
{
    // Curated Material Symbols suggestions for quest types (the icon is stored verbatim and rendered on the card).
    private static readonly (string Key, string Label)[] QuestIcons =
    [
        ("grass", "Gathering (grass)"), ("swords", "Combat (swords)"), ("crisis_alert", "Bounty (crisis_alert)"),
        ("local_shipping", "Delivery (local_shipping)"), ("shield", "Escort (shield)"), ("explore", "Exploration (explore)"),
        ("diamond", "Mining (diamond)"), ("build", "Crafting (build)"), ("storefront", "Trade (storefront)"),
        ("groups", "Raid (groups)"), ("recycling", "Salvage (recycling)"), ("inventory_2", "Recovery (inventory_2)"),
        ("flag", "Objective (flag)"), ("map", "Map (map)"), ("bolt", "Speed (bolt)"), ("science", "Research (science)"),
    ];

    private FeatureVerdict _gate;
    private IReadOnlyList<QuestType> _types = [];
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
            _gate = await scope.ServiceProvider.GetRequiredService<IFeatureGate>()
                .EvaluateAsync(GuildId, PlatformFeature.Quests);
        }
        if (!_gate.IsEnabled) { return; }
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        _types = await scope.ServiceProvider.GetRequiredService<QuestTypeService>().ListAsync(GuildId);
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
            var created = await scope.ServiceProvider.GetRequiredService<QuestTypeService>()
                .CreateAsync(GuildId, _newName!.Trim(), _newSort, _newIcon);
            Message = created is null
                ? "That name is blank or already a quest type."
                : $"Quest type “{created.Name}” added.";
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
                .ToCommandResult("Default quest types restored.").Message;
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
            var svc = scope.ServiceProvider.GetRequiredService<QuestTypeService>();
            var type = await svc.FindAsync(GuildId, id);
            if (type is null)
            {
                Message = "That quest type no longer exists.";
                return;
            }

            Message = await svc.EditAsync(type, m.Name, m.Sort, m.Icon)
                ? "Quest type updated."
                : "That name is blank or already a quest type.";
        }
        finally { _busy = false; await ReloadAsync(); }
    }

    private async Task DeleteAsync(Guid id, string name)
    {
        if (_busy || !await JS.InvokeAsync<bool>("confirm", new object?[] { $"Delete quest type “{name}”? Quests of that type lose it." }))
        {
            return;
        }

        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<QuestTypeService>();
            var type = await svc.FindAsync(GuildId, id);
            if (type is not null)
            {
                await svc.DeleteAsync(type);
            }

            Message = $"Quest type “{name}” deleted.";
        }
        finally { _busy = false; await ReloadAsync(); }
    }
}
