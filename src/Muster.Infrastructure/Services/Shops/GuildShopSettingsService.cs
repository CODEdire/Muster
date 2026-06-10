using Microsoft.Extensions.Options;
using Muster.Domain.Entities.Guilds;
using Muster.Persistence;

namespace Muster.Infrastructure.Services.Shops;

/// <summary>
/// Reads and writes a guild's <see cref="GuildShopSettings"/> row. When a guild has no row yet, reads return a
/// defaults instance built from <c>IOptions&lt;GuildShopSettings&gt;</c> (bound from AppConfig / appsettings under
/// <c>GuildDefaults:Shop</c>) — so callers always get a usable settings object, and the same defaults seed the row
/// the first time it's written. This is the table-per-feature + configured-bootstrap pattern (mirrors
/// <see cref="Musters.GuildMusterSettingsService"/>).
/// </summary>
public class GuildShopSettingsService(MusterDbContext db, IOptions<GuildShopSettings> defaults)
{
    /// <summary>The guild's row, or a (non-persisted) defaults instance when none exists.</summary>
    public async Task<GuildShopSettings> GetAsync(ulong guildId, CancellationToken ct = default)
        => await db.GuildShopSettings.FindAsync([guildId], ct) ?? Defaults(guildId);

    /// <summary>Load the guild's row (seeding it from defaults if missing), apply <paramref name="mutate"/>, save.</summary>
    public async Task<GuildShopSettings> UpsertAsync(ulong guildId, Action<GuildShopSettings> mutate, CancellationToken ct = default)
    {
        var row = await db.GuildShopSettings.FindAsync([guildId], ct);
        if (row is null)
        {
            row = Defaults(guildId);
            db.GuildShopSettings.Add(row);
        }

        mutate(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>A new settings instance seeded from the configured platform defaults (GuildId stamped on).</summary>
    private GuildShopSettings Defaults(ulong guildId)
    {
        var d = defaults.Value;
        return new GuildShopSettings
        {
            GuildId = guildId,
            PlayerMarketEnabled = d.PlayerMarketEnabled,
            OffersEnabled = d.OffersEnabled,
            TwoStepDelivery = d.TwoStepDelivery,
            RatingsEnabled = d.RatingsEnabled,
            PlayerTagsEnabled = d.PlayerTagsEnabled,
            RequireCategory = d.RequireCategory,
            MaxStoresPerSeller = d.MaxStoresPerSeller,
            MaxActiveListingsPerSeller = d.MaxActiveListingsPerSeller,
            MaxOpenOffersPerBuyer = d.MaxOpenOffersPerBuyer,
            MaxTagsPerListing = d.MaxTagsPerListing,
            MaxFeaturedPerStore = d.MaxFeaturedPerStore,
            CommissionBps = d.CommissionBps,
            FeaturedListingFee = d.FeaturedListingFee,
            MinPrice = d.MinPrice,
            MaxPrice = d.MaxPrice,
            AllowedCurrencyIds = [.. d.AllowedCurrencyIds],
            DeliveryConfirmTimeoutHours = d.DeliveryConfirmTimeoutHours,
            UndeliveredTimeoutHours = d.UndeliveredTimeoutHours,
            DisputeTimeoutHours = d.DisputeTimeoutHours,
            OfferExpiryHours = d.OfferExpiryHours,
            ListingDefaultExpiryDays = d.ListingDefaultExpiryDays,
            ListingCooldownMinutes = d.ListingCooldownMinutes,
            RatingWindowHours = d.RatingWindowHours,
            FeaturedDurationHours = d.FeaturedDurationHours,
            ShopChannelId = d.ShopChannelId,
            ShopModChannelId = d.ShopModChannelId,
        };
    }
}
