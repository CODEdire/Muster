using Microsoft.AspNetCore.Components;

namespace Muster.Web.Components.Pages.Economy.Participation.Parts;

/// <summary>Base for the embedded Participation tab bodies. The shell owns the header + tab bar and hands each part the
/// <see cref="GuildId"/>; the part loads its own data. Parts that re-render for different inputs (e.g. a season tab
/// switching between current/previous) override <see cref="ReloadKey"/> so the load re-runs when the key changes.</summary>
public abstract class ParticipationPart : ComponentBase
{
    [Parameter, EditorRequired] public ulong GuildId { get; set; }

    [Inject] protected IServiceScopeFactory Scopes { get; set; } = default!;

    protected bool _loading;

    /// <summary>When this changes between parameter sets, the part reloads. Null = load once.</summary>
    protected virtual object? ReloadKey => null;

    private object? _loadedKey = new();

    protected override async Task OnParametersSetAsync()
    {
        if (Equals(ReloadKey, _loadedKey))
        {
            return;
        }

        _loadedKey = ReloadKey;
        await ReloadAsync();
    }

    protected abstract Task ReloadAsync();
}
