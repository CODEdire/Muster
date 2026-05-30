using Muster.Bot.Economy;
using Muster.Bot.Questing;
using Muster.Domain.Enums;
using Xunit;

namespace Muster.Bot.UnitTests.Economy.Handlers;

/// <summary>The DM-receipt eligibility rule is pure, so we can assert which movements notify without Discord.</summary>
public class CurrencyDmHandlerTests
{
    private const ulong Member = 10;

    [Theory]
    [InlineData(nameof(CurrencyLedgerSource.Transfer))]
    [InlineData(nameof(CurrencyLedgerSource.Adjustment))]
    [InlineData(nameof(CurrencyLedgerSource.ManualAward))]
    public void Notifies_OnDeliberateGrantCredit(string source) =>
        Assert.True(CurrencyDmHandler.ShouldNotify(source, 50, Member));

    [Theory]
    [InlineData(nameof(CurrencyLedgerSource.Quest))]   // earning — has its own quest DMs
    [InlineData(nameof(CurrencyLedgerSource.Muster))]
    [InlineData(nameof(CurrencyLedgerSource.Event))]
    [InlineData(nameof(CurrencyLedgerSource.TrackingSession))]
    [InlineData(nameof(CurrencyLedgerSource.Connector))] // external echo
    [InlineData(nameof(CurrencyLedgerSource.Checkpoint))]
    public void Ignores_NonGrantSources(string source) =>
        Assert.False(CurrencyDmHandler.ShouldNotify(source, 50, Member));

    [Fact]
    public void Ignores_DebitLeg() => // the sender's negative leg of a transfer
        Assert.False(CurrencyDmHandler.ShouldNotify(nameof(CurrencyLedgerSource.Transfer), -50, Member));

    [Fact]
    public void Ignores_ZeroAmount() =>
        Assert.False(CurrencyDmHandler.ShouldNotify(nameof(CurrencyLedgerSource.Adjustment), 0, Member));

    [Fact]
    public void Ignores_HouseEscrowAccount() => // escrow credit leg of a bounty hold isn't a member receipt
        Assert.False(CurrencyDmHandler.ShouldNotify(nameof(CurrencyLedgerSource.Transfer), 50, 0));
}
