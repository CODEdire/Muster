using Microsoft.AspNetCore.Components;

namespace Muster.Web.Components.Pages.Economy.Treasury.Parts;

/// <summary>Base for the embedded Treasury tab bodies. The shell owns the header, currency switcher and tab bar and
/// hands each part the resolved <see cref="GuildId"/> + <see cref="Code"/>; the part loads its own data and reloads
/// whenever the currency (or the shell's <see cref="Revision"/>, bumped after a mint/adjust) changes.</summary>
public abstract class TreasuryPart : ComponentBase
{
    [Parameter, EditorRequired] public ulong GuildId { get; set; }
    [Parameter, EditorRequired] public string Code { get; set; } = "";

    /// <summary>Bumped by the shell to force a reload (e.g. after a mint/adjust) without changing the currency.</summary>
    [Parameter] public int Revision { get; set; }

    [Inject] protected IServiceScopeFactory Scopes { get; set; } = default!;

    protected bool _loading;
    private string? _loadedCode;
    private int _loadedRevision = -1;

    protected override async Task OnParametersSetAsync()
    {
        if (string.IsNullOrWhiteSpace(Code) || (Code == _loadedCode && Revision == _loadedRevision))
        {
            return;
        }

        _loadedCode = Code;
        _loadedRevision = Revision;
        await ReloadAsync();
    }

    protected abstract Task ReloadAsync();
}
