using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddShopTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GuildShopSettings",
                columns: table => new
                {
                    GuildId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    PlayerMarketEnabled = table.Column<bool>(type: "bit", nullable: false),
                    OffersEnabled = table.Column<bool>(type: "bit", nullable: false),
                    TwoStepDelivery = table.Column<bool>(type: "bit", nullable: false),
                    RatingsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    PlayerTagsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    RequireCategory = table.Column<bool>(type: "bit", nullable: false),
                    MaxStoresPerSeller = table.Column<int>(type: "int", nullable: false),
                    MaxActiveListingsPerSeller = table.Column<int>(type: "int", nullable: false),
                    MaxOpenOffersPerBuyer = table.Column<int>(type: "int", nullable: false),
                    MaxTagsPerListing = table.Column<int>(type: "int", nullable: false),
                    CommissionBps = table.Column<int>(type: "int", nullable: false),
                    MinPrice = table.Column<long>(type: "bigint", nullable: false),
                    MaxPrice = table.Column<long>(type: "bigint", nullable: false),
                    AllowedCurrencyIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DeliveryConfirmTimeoutHours = table.Column<int>(type: "int", nullable: false),
                    DisputeTimeoutHours = table.Column<int>(type: "int", nullable: false),
                    OfferExpiryHours = table.Column<int>(type: "int", nullable: false),
                    ListingDefaultExpiryDays = table.Column<int>(type: "int", nullable: false),
                    ListingCooldownMinutes = table.Column<int>(type: "int", nullable: false),
                    RatingWindowHours = table.Column<int>(type: "int", nullable: false),
                    ShopChannelId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    ShopModChannelId = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildShopSettings", x => x.GuildId);
                    table.ForeignKey(
                        name: "FK_GuildShopSettings_Guilds_GuildId",
                        column: x => x.GuildId,
                        principalTable: "Guilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShopCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GuildId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Sort = table.Column<int>(type: "int", nullable: false),
                    CommissionBpsOverride = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopCategories_Guilds_GuildId",
                        column: x => x.GuildId,
                        principalTable: "Guilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShopStores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GuildId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    OwnerId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    BannerImageKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AccentColor = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Closed = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopStores", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopStores_Guilds_GuildId",
                        column: x => x.GuildId,
                        principalTable: "Guilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShopListings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GuildId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    StoreId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SellerId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ImageKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ThumbKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CurrencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Price = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StatusChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopListings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopListings_ShopStores_StoreId",
                        column: x => x.StoreId,
                        principalTable: "ShopStores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShopListingTags",
                columns: table => new
                {
                    ListingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Tag = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopListingTags", x => new { x.ListingId, x.Tag });
                    table.ForeignKey(
                        name: "FK_ShopListingTags_ShopListings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "ShopListings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShopCategories_GuildId_Name",
                table: "ShopCategories",
                columns: new[] { "GuildId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShopListings_CategoryId",
                table: "ShopListings",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopListings_GuildId_SellerId",
                table: "ShopListings",
                columns: new[] { "GuildId", "SellerId" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopListings_GuildId_Status",
                table: "ShopListings",
                columns: new[] { "GuildId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopListings_StoreId",
                table: "ShopListings",
                column: "StoreId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopListingTags_Tag",
                table: "ShopListingTags",
                column: "Tag");

            migrationBuilder.CreateIndex(
                name: "IX_ShopStores_GuildId_OwnerId",
                table: "ShopStores",
                columns: new[] { "GuildId", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopStores_GuildId_Slug",
                table: "ShopStores",
                columns: new[] { "GuildId", "Slug" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GuildShopSettings");

            migrationBuilder.DropTable(
                name: "ShopCategories");

            migrationBuilder.DropTable(
                name: "ShopListingTags");

            migrationBuilder.DropTable(
                name: "ShopListings");

            migrationBuilder.DropTable(
                name: "ShopStores");
        }
    }
}
