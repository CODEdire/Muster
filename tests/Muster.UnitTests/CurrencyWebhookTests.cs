using Muster.Domain.Entities.Currencies;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Xunit;

namespace Muster.UnitTests;

/// <summary>The webhook filter + health-folding rules are pure, so we can assert them without HTTP.</summary>
public class CurrencyWebhookTests
{
    [Fact]
    public void Matches_EmptyFilter_AcceptsEverySource()
    {
        var hook = new CurrencyWebhook { SourceFilter = [] };
        Assert.True(hook.Matches(CurrencyLedgerSource.Transfer));
        Assert.True(hook.Matches(CurrencyLedgerSource.Quest));
    }

    [Fact]
    public void Matches_WithFilter_OnlyListedSources()
    {
        var hook = new CurrencyWebhook { SourceFilter = [CurrencyLedgerSource.Transfer, CurrencyLedgerSource.Adjustment] };
        Assert.True(hook.Matches(CurrencyLedgerSource.Transfer));
        Assert.False(hook.Matches(CurrencyLedgerSource.Quest));
    }

    [Fact]
    public void ApplyHealth_Success_ResetsFailures()
    {
        var hook = new CurrencyWebhook { ConsecutiveFailures = 3, LastError = "boom", Enabled = true };
        CurrencyWebhookDispatcher.ApplyHealth(hook, new WebhookDelivery(true, 200, null));

        Assert.Equal(0, hook.ConsecutiveFailures);
        Assert.Null(hook.LastError);
        Assert.Equal(200, hook.LastStatusCode);
        Assert.True(hook.Enabled);
    }

    [Fact]
    public void ApplyHealth_AutoDisables_AfterThreshold()
    {
        var hook = new CurrencyWebhook { ConsecutiveFailures = CurrencyWebhookDispatcher.DisableAfterFailures - 1, Enabled = true };
        CurrencyWebhookDispatcher.ApplyHealth(hook, new WebhookDelivery(false, 500, "HTTP 500"));

        Assert.Equal(CurrencyWebhookDispatcher.DisableAfterFailures, hook.ConsecutiveFailures);
        Assert.False(hook.Enabled); // tripped the failure threshold
        Assert.Equal("HTTP 500", hook.LastError);
    }
}
