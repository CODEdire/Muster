using Microsoft.JSInterop;
using Muster.Infrastructure.Services.Platform;

namespace Muster.Web;

/// <summary>
/// Per-circuit cache of the viewer's browser IANA time zone (from <c>Intl</c>), resolved once via JS and shared
/// by every component that shows a time — so one detection serves the whole circuit. Scoped. Raises
/// <see cref="Changed"/> when it resolves so components that already rendered can re-localize.
/// </summary>
public sealed class BrowserTimeZone
{
    private bool _resolved;

    /// <summary>The detected IANA zone, or null until resolved / when unavailable (static SSR, JS blocked).</summary>
    public string? Zone { get; private set; }

    public event Action? Changed;

    /// <summary>Detect the browser zone once (idempotent — safe to call from many components every render).</summary>
    public async Task EnsureAsync(IJSRuntime js)
    {
        if (_resolved)
        {
            return;
        }

        _resolved = true;
        try
        {
            var zone = await js.InvokeAsync<string?>("musterBrowserTimeZone");
            if (TimeZoneService.IsValidZone(zone))
            {
                Zone = zone;
            }
        }
        catch
        {
            // No circuit / JS blocked — leave null; callers fall back to the server-resolved zone.
        }

        Changed?.Invoke();
    }
}
