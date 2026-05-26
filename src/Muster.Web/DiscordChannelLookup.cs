using System.Net.Http.Json;

namespace Muster.Web;

/// <summary>
/// Lists a guild's postable text channels live from Discord (bot token), so the settings page can offer a channel
/// picker without syncing channels into a table. Best-effort: returns an empty list if the token is missing or
/// Discord can't be reached, so the settings page degrades to "no channels" rather than erroring.
/// </summary>
public sealed class DiscordChannelLookup(HttpClient http, IConfiguration config, ILogger<DiscordChannelLookup> logger)
{
    public record ChannelOption(ulong Id, string Name);

    public async Task<IReadOnlyList<ChannelOption>> GetTextChannelsAsync(ulong guildId, CancellationToken ct = default)
    {
        var token = config["Discord:Token"];
        if (string.IsNullOrWhiteSpace(token))
        {
            return [];
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://discord.com/api/v10/guilds/{guildId}/channels");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bot {token}");

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Discord channel list for guild {GuildId} returned {Status}.", guildId, resp.StatusCode);
                return [];
            }

            var channels = await resp.Content.ReadFromJsonAsync<List<ChannelDto>>(ct) ?? [];
            return channels
                .Where(c => c.Type is 0 or 5) // GUILD_TEXT (0) and GUILD_ANNOUNCEMENT (5) are postable
                .Select(c => new ChannelOption(ulong.Parse(c.Id), c.Name ?? c.Id))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to list Discord channels for guild {GuildId}.", guildId);
            return [];
        }
    }

    private sealed record ChannelDto(string Id, string? Name, int Type);
}
