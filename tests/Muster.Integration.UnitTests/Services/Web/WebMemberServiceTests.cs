using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Xunit;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Web;

namespace Muster.IntegrationTests;

public class WebMemberServiceTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    [Fact]
    public async Task GetMember_ReturnsDisplayName_Wallets_AndHistory()
    {
        using var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1);
        await new MemberSyncService(db).UpsertAsync(1, 10, "alice", "Alice", null, nickname: "Ace");

        var points = await db.Currencies.SingleAsync(c => c.Code == "POINTS");
        var awards = new CurrencyService(db, new RecordingMessageBus());
        await awards.AwardAsync(1, 10, points.Id, 30, CurrencyLedgerSource.ManualAward, "a", "great work");
        await awards.AwardAsync(1, 10, points.Id, 20, CurrencyLedgerSource.Muster, "m", "check-in");

        var sut = new WebMemberService(db, new CurrencyReadService(db));
        var detail = await sut.GetAsync(1, 10);

        Assert.Equal("Ace", detail.DisplayName); // nickname preferred
        Assert.Equal(50, detail.Wallets.Single(w => w.CurrencyCode == "POINTS").Balance);
        Assert.Equal(2, detail.History.Count);
        Assert.Equal("check-in", detail.History[0].Reason); // newest first
    }

    [Fact]
    public async Task GetMember_HistoryFilter_ScopesToOneCurrency()
    {
        using var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1);
        db.Currencies.Add(new Muster.Domain.Entities.Currencies.Currency
        {
            Id = Guid.NewGuid(), GuildId = 1, Code = "COIN", Name = "Coin", IsSpendable = true,
        });
        await db.SaveChangesAsync();

        var awards = new CurrencyService(db, new RecordingMessageBus());
        await awards.AwardPointsAsync(1, 10, 30, CurrencyLedgerSource.Quest, "q", "win");
        await awards.MintAsync(1, "COIN", 10, 100, "drop");

        var sut = new WebMemberService(db, new CurrencyReadService(db));

        Assert.Equal(2, (await sut.GetAsync(1, 10)).History.Count);
        var coinOnly = await sut.GetAsync(1, 10, currencyFilter: "COIN");
        Assert.Single(coinOnly.History);
        Assert.Equal("COIN", coinOnly.History[0].Currency);
    }

    [Fact]
    public async Task GetRecipients_ExcludesSelf_AndBots()
    {
        using var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1);
        var sync = new MemberSyncService(db);
        await sync.UpsertAsync(1, 10, "alice", "Alice", null);
        await sync.UpsertAsync(1, 20, "bob", "Bob", null);
        await sync.UpsertAsync(1, 99, "loot-bot", "Loot", null, isBot: true);

        var recipients = await new WebMemberService(db, new CurrencyReadService(db)).GetRecipientsAsync(1, excludeUserId: 10);

        Assert.DoesNotContain(recipients, r => r.UserId == 10); // self excluded
        Assert.DoesNotContain(recipients, r => r.UserId == 99); // bot excluded
        Assert.Contains(recipients, r => r.UserId == 20);
    }
}
