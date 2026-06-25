using Microsoft.AspNetCore.Components;

namespace Muster.Web.Components.Pages.Economy.Wallet.Parts;

/// <summary>Base for the embedded wallet tab bodies. The shell (<c>GuildWallet</c>) owns the header, currency switcher
/// and tab bar, resolves the target member (an admin / economy-manager / auditor may view another via ?member=) and
/// hands each part the <see cref="GuildId"/> + <see cref="TargetUserId"/>. The part loads its own data and reloads
/// when <see cref="ReloadKey"/> changes (target member, selected currency, or a mint/adjust revision).</summary>
public abstract class WalletPart : ComponentBase
{
    [Parameter, EditorRequired] public ulong GuildId { get; set; }
    [Parameter, EditorRequired] public ulong TargetUserId { get; set; }

    [Inject] protected IServiceScopeFactory Scopes { get; set; } = default!;

    protected bool _loading;

    /// <summary>Reload when this changes between parameter sets. Default = the target member.</summary>
    protected virtual object? ReloadKey => TargetUserId;

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
