using System.Security.Claims;

namespace Muster.Web;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The signed-in user's Discord snowflake, from the OAuth NameIdentifier claim.</summary>
    public static ulong? GetDiscordUserId(this ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(ClaimTypes.NameIdentifier);
        return ulong.TryParse(value, out var id) ? id : null;
    }

    public static string DisplayName(this ClaimsPrincipal user)
        => user.Identity?.Name ?? "there";
}
