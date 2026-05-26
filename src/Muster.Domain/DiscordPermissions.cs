namespace Muster.Domain;

/// <summary>
/// The subset of Discord permission bits Muster cares about for the admin bypass. Values match
/// Discord's permission bitfield and are stable.
/// </summary>
public static class DiscordPermissions
{
    public const ulong Administrator = 0x8;
    public const ulong ManageGuild = 0x20;

    /// <summary>Permissions that grant an implicit bot-admin bypass (so server managers can't be locked out).</summary>
    public const ulong AdminBypassMask = Administrator | ManageGuild;
}
