using Muster.Domain.Entities;
using Muster.Persistence.Queries;
using Xunit;

namespace Muster.Persistence.UnitTests.Queries;

public class MembershipQueriesTests
{
    [Fact]
    public async Task GetSettingsAsync_ReturnsDefaults_WhenGuildUnknown()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;

        var settings = await db.GetSettingsAsync(404);

        Assert.NotNull(settings);
        Assert.NotNull(settings.Quests); // nested defaults are materialized, not null
    }

    [Fact]
    public async Task GetSettingsAsync_ReturnsStoredSettings_WhenGuildKnown()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        db.Guilds.Add(new Guild { Id = 1, Name = "G", Settings = new GuildSettings { PointsPerVoiceMinute = 7 } });
        await db.SaveChangesAsync();

        Assert.Equal(7, (await db.GetSettingsAsync(1)).PointsPerVoiceMinute);
    }

    [Fact]
    public async Task IsGuildOwner_And_IsMember_ScopeToTheGuild()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        db.Guilds.Add(new Guild { Id = 1, Name = "G", OwnerId = 5 });
        db.Users.Add(new DiscordUser { Id = 10, Username = "u" }); // GuildMember FKs the user
        db.GuildMembers.Add(new GuildMember { GuildId = 1, UserId = 10, JoinedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        Assert.True(await db.IsGuildOwnerAsync(1, 5));
        Assert.False(await db.IsGuildOwnerAsync(1, 10)); // member, not owner
        Assert.True(await db.IsMemberAsync(1, 10));
        Assert.False(await db.IsMemberAsync(1, 99));     // not a member
    }

    [Fact]
    public async Task CurrencyDmOptOut_RoundTrips_AndDefaultsOff()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        db.Users.Add(new DiscordUser { Id = 10, Username = "u" });
        await db.SaveChangesAsync();

        Assert.False(await db.CurrencyDmOptOutAsync(10));          // default = receipts on
        Assert.False(await db.CurrencyDmOptOutAsync(404));         // unknown user → default

        Assert.True(await db.SetCurrencyDmOptOutAsync(10, true));  // opt out
        Assert.True(await db.CurrencyDmOptOutAsync(10));

        Assert.False(await db.SetCurrencyDmOptOutAsync(10, false)); // back on
        Assert.False(await db.CurrencyDmOptOutAsync(10));
    }

    [Fact]
    public async Task RolePermissionsAsync_ReturnsBitmasksForTheRequestedRolesOnly()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        db.GuildRoles.AddRange(
            new GuildRole { GuildId = 1, RoleId = 100, Name = "Admin", Permissions = 8 },
            new GuildRole { GuildId = 1, RoleId = 200, Name = "Mod", Permissions = 32 },
            new GuildRole { GuildId = 1, RoleId = 300, Name = "Member", Permissions = 0 });
        await db.SaveChangesAsync();

        var perms = await db.RolePermissionsAsync(1, [100, 200]);

        Assert.Equal(2, perms.Count);
        Assert.Contains(8ul, perms);
        Assert.Contains(32ul, perms);
    }
}
