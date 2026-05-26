using System.Text.Json;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Connectors;
using Xunit;

namespace Muster.IntegrationTests;

/// <summary>Pure bits of the v2 currency connector: signing, the dispatch gate, body templating, balance parsing.</summary>
public class ConnectorClientTests
{
    [Fact]
    public void Sign_Sha256_MatchesKnownVector()
    {
        // HMAC-SHA256(key="key", "The quick brown fox jumps over the lazy dog")
        Assert.Equal(
            "sha256=f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8",
            CurrencyConnectorClient.Sign(ConnectorSignAlgorithm.HmacSha256, "key", "The quick brown fox jumps over the lazy dog"));
    }

    [Fact]
    public void Sign_Sha512_IsPrefixedAndDeterministic_AndInputSensitive()
    {
        var a = CurrencyConnectorClient.Sign(ConnectorSignAlgorithm.HmacSha512, "secret", "{\"amount\":5}");
        Assert.StartsWith("sha512=", a);
        Assert.Equal(7 + 128, a.Length); // "sha512=" + 128 hex chars
        Assert.Equal(a, CurrencyConnectorClient.Sign(ConnectorSignAlgorithm.HmacSha512, "secret", "{\"amount\":5}"));
        Assert.NotEqual(a, CurrencyConnectorClient.Sign(ConnectorSignAlgorithm.HmacSha512, "other", "{\"amount\":5}"));
    }

    [Theory]
    // mode, enabled, sourceType → expected
    [InlineData(CurrencyMode.External, true, CurrencyLedgerSource.Quest, true)]
    [InlineData(CurrencyMode.Hybrid, true, CurrencyLedgerSource.ManualAward, true)]
    [InlineData(CurrencyMode.Internal, true, CurrencyLedgerSource.Quest, false)]   // internal never pushes
    [InlineData(CurrencyMode.External, false, CurrencyLedgerSource.Quest, false)]  // disabled
    [InlineData(CurrencyMode.External, true, CurrencyLedgerSource.Connector, false)] // loop guard
    public void ShouldDispatch_GatesCorrectly(CurrencyMode mode, bool enabled, CurrencyLedgerSource source, bool expected)
        => Assert.Equal(expected, CurrencyConnectorClient.ShouldDispatch(mode, new CurrencyConnector { Enabled = enabled }, source));

    [Fact]
    public void Template_IsValidJson_AndRendersCruorBody_WithCorrectTypes()
    {
        var msg = new ConnectorDispatch(1, "CRUOR", 456, 50, "Quest approved", "Quest",
            DateTimeOffset.UnixEpoch, DeliveryId: 9876, DisplayName: "Ayla");
        // Quoted numeric placeholders keep the *template* itself valid JSON.
        const string template = "{\"member_id\": \"$userId\", \"display_name\": \"$displayName\", \"cruor_amount\": \"$amount\"}";
        Assert.True(IsValidJson(template)); // editor-friendly: parses before rendering

        using var doc = JsonDocument.Parse(ConnectorTemplate.Render(template, msg));
        Assert.Equal(456, doc.RootElement.GetProperty("member_id").GetInt64());     // quotes stripped → number
        Assert.Equal("Ayla", doc.RootElement.GetProperty("display_name").GetString());
        Assert.Equal(50, doc.RootElement.GetProperty("cruor_amount").GetInt64());
    }

    [Fact]
    public void Template_AllowsNegativeAmounts_AndEscapesStrings()
    {
        var msg = new ConnectorDispatch(1, "CRUOR", 7, -25, "spent on \"loot\"", "Connector",
            DateTimeOffset.UnixEpoch, 1, DisplayName: "A\"B");
        const string template = "{\"amount\": \"$amount\", \"who\": \"$displayName\", \"why\": \"$reason\"}";

        using var doc = JsonDocument.Parse(ConnectorTemplate.Render(template, msg));
        Assert.Equal(-25, doc.RootElement.GetProperty("amount").GetInt64());
        Assert.Equal("A\"B", doc.RootElement.GetProperty("who").GetString());
        Assert.Equal("spent on \"loot\"", doc.RootElement.GetProperty("why").GetString());
    }

    private static bool IsValidJson(string s)
    {
        try { using var _ = JsonDocument.Parse(s); return true; }
        catch (JsonException) { return false; }
    }

    [Fact]
    public void IsDashboardSyncDue_TrueWhenNullOrOlderThanFiveMinutes()
    {
        var now = DateTimeOffset.UnixEpoch.AddHours(1);
        Assert.True(CurrencyConnectorSyncService.IsDashboardSyncDue(null, now));
        Assert.True(CurrencyConnectorSyncService.IsDashboardSyncDue(now.AddMinutes(-6), now));
        Assert.False(CurrencyConnectorSyncService.IsDashboardSyncDue(now.AddMinutes(-2), now));
    }

    [Fact]
    public void RenderUrl_SubstitutesTokens_AndUrlEncodesStrings()
    {
        var msg = new ConnectorDispatch(9, "COIN", 123, 50, "r", "Quest", DateTimeOffset.UnixEpoch, 1, DisplayName: "A B");
        Assert.Equal("user=123&name=A%20B&amt=50", ConnectorTemplate.RenderUrl("user=$userId&name=$displayName&amt=$amount", msg));
    }

    [Fact]
    public void ParseResponseValue_Regex_CapturesNumberFromText()
        => Assert.Equal(50L, CurrencyConnectorClient.ParseResponseValue(ConnectorResponseFormat.Regex, @"balance:\s*(\d+)", "Your balance: 50 cruor"));

    [Theory]
    [InlineData(ConnectorResponseFormat.Text, null, "  42 ", 42L)]      // plain text, trimmed
    [InlineData(ConnectorResponseFormat.Text, null, "nope", null)]      // non-numeric text
    [InlineData(ConnectorResponseFormat.Json, "data.amount", "{\"data\":{\"amount\":7}}", 7L)] // nested json path
    [InlineData(ConnectorResponseFormat.None, null, "5", null)]         // ignored
    public void ParseResponseValue_HandlesFormats(ConnectorResponseFormat format, string? path, string body, long? expected)
        => Assert.Equal(expected, CurrencyConnectorClient.ParseResponseValue(format, path, body));

    [Theory]
    [InlineData("{\"balance\": 42}", "balance", 42)]
    [InlineData("{\"data\":{\"amount\": 99}}", "data.amount", 99)]
    [InlineData("{\"balance\": \"123\"}", "balance", 123)]       // numeric string
    [InlineData("{\"nope\": 1}", "balance", null)]               // missing path
    public void ReadBalance_WalksDottedPath(string json, string path, int? expected)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected is { } e ? e : (long?)null, CurrencyConnectorClient.ReadBalance(doc.RootElement, path));
    }
}
