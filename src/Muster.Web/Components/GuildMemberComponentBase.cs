using Microsoft.AspNetCore.Components;
using Muster.Infrastructure.Services;

namespace Muster.Web.Components;

/// <summary>Base for pages any guild member can view (dashboard, own profile). Session/403 handling from the base.</summary>
public abstract class GuildMemberComponentBase : GuildPageComponentBase
{
    [Inject] protected WebGuildService Guilds { get; set; } = default!;

    protected override Task<bool> IsAuthorizedAsync(ulong guildId, ulong userId)
        => Guilds.CanViewGuildAsync(guildId, userId);
}
