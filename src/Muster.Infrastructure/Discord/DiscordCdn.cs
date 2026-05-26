namespace Muster.Infrastructure.Discord;

/// <summary>Discord CDN URL helpers shared by the read services that surface user avatars.</summary>
public static class DiscordCdn
{
    /// <summary>Avatar URL for a user; animated hashes (a_…) are gifs, the rest png. Null hash ⇒ null.</summary>
    public static string? AvatarUrl(ulong userId, string? hash, int size = 64) =>
        string.IsNullOrEmpty(hash)
            ? null
            : $"https://cdn.discordapp.com/avatars/{userId}/{hash}.{(hash.StartsWith("a_", StringComparison.Ordinal) ? "gif" : "png")}?size={size}";
}
