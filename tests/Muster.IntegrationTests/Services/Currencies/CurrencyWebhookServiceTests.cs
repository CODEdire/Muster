using Microsoft.EntityFrameworkCore;
using Muster.Domain.Enums;
using Muster.Infrastructure.Connectors;
using Muster.Infrastructure.Services.Currencies;
using Muster.Persistence;
using Xunit;

namespace Muster.IntegrationTests;

public class CurrencyWebhookServiceTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    // Identity protector so we can assert the secret is stored (and never surfaced) without Data Protection wiring.
    private sealed class FakeProtector : IConnectorSecretProtector
    {
        public string? Protect(string? plaintext) => plaintext is null ? null : $"enc:{plaintext}";
        public string? Unprotect(string? ciphertext) => ciphertext?.Replace("enc:", "");
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static CurrencyWebhookService Sut(MusterDbContext db)
    {
        var protector = new FakeProtector();
        var dispatcher = new CurrencyWebhookDispatcher(new StubHttpClientFactory(), protector);
        return new CurrencyWebhookService(db, protector, dispatcher);
    }

    [Fact]
    public async Task Create_RejectsNonHttpsUrl()
    {
        using var db = NewDb();
        var result = await Sut(db).CreateAsync(1, "http://insecure.example", "s", []);

        Assert.True(result.IsError);
        Assert.Empty(db.CurrencyWebhooks);
    }

    [Fact]
    public async Task Create_StoresEncryptedSecret_AndNeverReturnsIt()
    {
        using var db = NewDb();
        var sut = Sut(db);

        var result = await sut.CreateAsync(1, "https://example.com/hook", "shhh", [CurrencyLedgerSource.Transfer]);
        Assert.False(result.IsError);

        var stored = await db.CurrencyWebhooks.SingleAsync();
        Assert.Equal("enc:shhh", stored.Secret);                 // encrypted at rest
        Assert.Equal([CurrencyLedgerSource.Transfer], stored.SourceFilter);

        var view = (await sut.ListAsync(1)).Single();
        Assert.True(view.HasSecret);                              // surfaced as a flag only
        Assert.Equal("https://example.com/hook", view.Url);
    }

    [Fact]
    public async Task Update_BlankSecret_KeepsExisting()
    {
        using var db = NewDb();
        var sut = Sut(db);
        var id = (await sut.CreateAsync(1, "https://a.example/h", "keep", [])).Id!.Value;

        await sut.UpdateAsync(1, id, "https://b.example/h", secret: null, [CurrencyLedgerSource.Quest]);

        var stored = await db.CurrencyWebhooks.SingleAsync();
        Assert.Equal("https://b.example/h", stored.Url);
        Assert.Equal("enc:keep", stored.Secret);                  // unchanged
        Assert.Equal([CurrencyLedgerSource.Quest], stored.SourceFilter);
    }

    [Fact]
    public async Task SetEnabled_ReEnable_ClearsFailureStreak()
    {
        using var db = NewDb();
        var sut = Sut(db);
        var id = (await sut.CreateAsync(1, "https://a.example/h", null, [])).Id!.Value;
        var hook = await db.CurrencyWebhooks.SingleAsync();
        hook.Enabled = false;
        hook.ConsecutiveFailures = 20;
        hook.LastError = "boom";
        await db.SaveChangesAsync();

        await sut.SetEnabledAsync(1, id, true);

        var reloaded = await db.CurrencyWebhooks.SingleAsync();
        Assert.True(reloaded.Enabled);
        Assert.Equal(0, reloaded.ConsecutiveFailures);
        Assert.Null(reloaded.LastError);
    }

    [Fact]
    public async Task Delete_RemovesTheWebhook()
    {
        using var db = NewDb();
        var sut = Sut(db);
        var id = (await sut.CreateAsync(1, "https://a.example/h", null, [])).Id!.Value;

        await sut.DeleteAsync(1, id);

        Assert.Empty(db.CurrencyWebhooks);
    }
}
