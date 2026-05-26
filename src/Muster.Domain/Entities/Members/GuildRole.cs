namespace Muster.Domain.Entities.Members;

/// <summary>A Discord role snapshot, used to resolve permissions (Administrator / Manage Guild).</summary>
public class GuildRole
{
    public ulong GuildId { get; set; }
    public ulong RoleId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Raw Discord permission bitfield for the role.</summary>
    public ulong Permissions { get; set; }
}
