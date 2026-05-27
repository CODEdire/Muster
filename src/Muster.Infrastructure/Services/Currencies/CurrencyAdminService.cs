using System.Text.RegularExpressions;
using Muster.Domain;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Connectors;

namespace Muster.Infrastructure.Services.Currencies;

/// <summary>Connector config for the admin UI. <see cref="Config"/> has its secrets blanked (write-only); the
/// <c>*HasSecret</c> flags tell the UI whether one is already stored.</summary>
public record CurrencyConnectorView(CurrencyConnector Config, bool AuthHasSecret, bool SignHasSecret);

public record CurrencyView(Guid Id, string Code, string Name, bool IsSeasonal, bool IsSpendable, CurrencyMode Mode, bool IsSystem, CurrencyConnectorView Connector);

/// <summary>Admin management of a guild's currencies + their outbound connectors. Callers depend on this interface.</summary>
public interface ICurrencyAdminService
{
    /// <summary>A guild's currencies projected for the admin list (connector secrets blanked).</summary>
    Task<IReadOnlyList<CurrencyView>> ListAsync(ulong guildId, CancellationToken ct = default);

    /// <summary>One currency view by id (for the edit page), or null.</summary>
    Task<CurrencyView?> GetAsync(ulong guildId, Guid currencyId, CancellationToken ct = default);

    /// <summary>Create a new (non-system) currency.</summary>
    Task<CommandResult> CreateAsync(ulong guildId, string code, string name, bool isSeasonal, bool isSpendable, CurrencyMode mode, CancellationToken ct = default);

    /// <summary>Update mutable fields (name, spendable, mode); code + seasonal nature are fixed at creation.</summary>
    Task<CommandResult> UpdateAsync(ulong guildId, Guid currencyId, string name, bool isSpendable, CurrencyMode mode, CancellationToken ct = default);

    /// <summary>Replace a currency's connector config (secrets are write-only / kept when blank).</summary>
    Task<CommandResult> SetConnectorAsync(ulong guildId, Guid currencyId, CurrencyConnector incoming, CancellationToken ct = default);

    /// <summary>Run the connector's Credit action with a synthetic movement to verify the endpoint/auth.</summary>
    Task<CommandResult> TestConnectorAsync(ulong guildId, Guid currencyId, CancellationToken ct = default);

    /// <summary>Read a member's external balance via the connector's GetBalance action (null if unconfigured/failed).</summary>
    Task<long?> ReadExternalBalanceAsync(ulong guildId, Guid currencyId, ulong userId, CancellationToken ct = default);
}

/// <summary>
/// Admin management of a guild's currencies. The seeded POINTS currency is treated as a protected
/// system currency (its code and seasonal nature can't change). Other currencies (e.g. a spendable
/// COIN for bounties/loot) are created and tuned here.
/// </summary>
public partial class CurrencyAdminService(
    MusterDbContext db, IConnectorSecretProtector? secrets = null, CurrencyConnectorClient? client = null) : ICurrencyAdminService
{
    public const string PointsCode = CurrencyCodes.PointsCode;

    [GeneratedRegex(@"^[A-Z0-9_]{2,16}$")]
    private static partial Regex CodePattern();

    public async Task<IReadOnlyList<CurrencyView>> ListAsync(ulong guildId, CancellationToken ct = default)
    {
        var currencies = await db.ListCurrenciesReadOnlyAsync(guildId, ct);

        return currencies.Select(c => new CurrencyView(
            c.Id, c.Code, c.Name, c.IsSeasonal, c.IsSpendable, c.Mode, c.Code == PointsCode, ToConnectorView(c.Connector))).ToList();
    }

    /// <summary>One currency view by id (for the edit page), or null.</summary>
    public async Task<CurrencyView?> GetAsync(ulong guildId, Guid currencyId, CancellationToken ct = default)
    {
        var c = await db.FindCurrencyByIdReadOnlyAsync(guildId, currencyId, ct);
        return c is null
            ? null
            : new CurrencyView(c.Id, c.Code, c.Name, c.IsSeasonal, c.IsSpendable, c.Mode, c.Code == PointsCode, ToConnectorView(c.Connector));
    }

    /// <summary>Blank the secrets (write-only) but report whether each is set.</summary>
    private static CurrencyConnectorView ToConnectorView(CurrencyConnector c)
    {
        c.Normalize(); // old rows may have null nested owned objects
        var authHasSecret = !string.IsNullOrEmpty(c.Auth.Secret);
        var signHasSecret = !string.IsNullOrEmpty(c.Signing.Secret);
        c.Auth.Secret = null;
        c.Signing.Secret = null;
        return new CurrencyConnectorView(c, authHasSecret, signHasSecret);
    }

    public async Task<CommandResult> CreateAsync(
        ulong guildId, string code, string name, bool isSeasonal, bool isSpendable, CurrencyMode mode,
        CancellationToken ct = default)
    {
        code = (code ?? string.Empty).Trim().ToUpperInvariant();

        if (!CodePattern().IsMatch(code))
        {
            return CommandResult.Error("Code must be 2–16 characters: A–Z, 0–9, or underscore.");
        }

        if (code == PointsCode)
        {
            return CommandResult.Error("POINTS is reserved.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return CommandResult.Error("Please provide a name.");
        }

        if (await db.CurrencyExistsAsync(guildId, code, ct))
        {
            return CommandResult.Error($"A currency with code {code} already exists.");
        }

        db.Currencies.Add(new Currency
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            Code = code,
            Name = name.Trim(),
            IsSeasonal = isSeasonal,
            IsSpendable = isSpendable,
            Mode = mode,
        });
        await db.SaveChangesAsync(ct);

        return CommandResult.Ok($"Created currency **{code}** ({name.Trim()}).");
    }

    /// <summary>Update mutable fields (name, spendable, mode). Code and seasonal nature are fixed at creation.</summary>
    public async Task<CommandResult> UpdateAsync(
        ulong guildId, Guid currencyId, string name, bool isSpendable, CurrencyMode mode, CancellationToken ct = default)
    {
        var currency = await db.FindCurrencyByIdAsync(guildId, currencyId, ct);
        if (currency is null)
        {
            return CommandResult.Error("Currency not found.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return CommandResult.Error("Please provide a name.");
        }

        currency.Name = name.Trim();
        currency.IsSpendable = isSpendable;
        currency.Mode = mode;
        await db.SaveChangesAsync(ct);

        return CommandResult.Ok($"Updated currency **{currency.Code}**.");
    }

    /// <summary>Replace a currency's connector config. Secrets (<c>Auth.Secret</c>, <c>Signing.Secret</c>) are
    /// write-only and encrypted: a blank incoming secret keeps the stored one; a non-blank one is encrypted.</summary>
    public async Task<CommandResult> SetConnectorAsync(ulong guildId, Guid currencyId, CurrencyConnector incoming, CancellationToken ct = default)
    {
        var currency = await db.FindCurrencyByIdAsync(guildId, currencyId, ct);
        if (currency is null)
        {
            return CommandResult.Error("Currency not found.");
        }

        var existing = currency.Connector.Normalize();
        incoming.Normalize();

        // Auth.Secret / Signing.Secret: blank = keep current (already encrypted); else encrypt the new value.
        incoming.Auth.Secret = string.IsNullOrWhiteSpace(incoming.Auth.Secret)
            ? existing.Auth.Secret
            : (secrets?.Protect(incoming.Auth.Secret.Trim()) ?? incoming.Auth.Secret.Trim());
        incoming.Signing.Secret = string.IsNullOrWhiteSpace(incoming.Signing.Secret)
            ? existing.Signing.Secret
            : (secrets?.Protect(incoming.Signing.Secret.Trim()) ?? incoming.Signing.Secret.Trim());

        currency.Connector = incoming;
        await db.SaveChangesAsync(ct);

        return CommandResult.Ok($"Updated **{currency.Code}** connector.");
    }

    /// <summary>Run the connector's Credit action with a synthetic movement to verify the endpoint/auth work.</summary>
    public async Task<CommandResult> TestConnectorAsync(ulong guildId, Guid currencyId, CancellationToken ct = default)
    {
        if (client is null)
        {
            return CommandResult.Error("Connector client isn't available in this context.");
        }

        var currency = await db.FindCurrencyByIdAsync(guildId, currencyId, ct);
        if (currency is null)
        {
            return CommandResult.Error("Currency not found.");
        }

        var probe = new ConnectorDispatch(guildId, currency.Code, 0, 1, "Muster connector test", "Test", DateTimeOffset.UtcNow, 0, "Muster");
        var result = await client.ExecuteAsync(currency.Connector, ConnectorActionKind.Credit, probe, ct);

        // Persist last-delivery health so the admin page reflects the test (success or failure).
        CurrencyConnectorClient.RecordHealth(currency.Connector, result);
        await db.SaveChangesAsync(ct);

        return result.Success
            ? CommandResult.Ok($"Test Credit succeeded (HTTP {result.StatusCode}).")
            : CommandResult.Error($"Test failed: {result.Error}");
    }

    /// <summary>Read the external balance for a member via the connector's GetBalance action (null if unconfigured/failed).</summary>
    public async Task<long?> ReadExternalBalanceAsync(ulong guildId, Guid currencyId, ulong userId, CancellationToken ct = default)
    {
        if (client is null)
        {
            return null;
        }

        var currency = await db.FindCurrencyByIdAsync(guildId, currencyId, ct);
        if (currency is null)
        {
            return null;
        }

        var probe = new ConnectorDispatch(guildId, currency.Code, userId, 0, "balance", "Test", DateTimeOffset.UtcNow, 0);
        return await client.GetBalanceAsync(currency.Connector, probe, ct);
    }
}
