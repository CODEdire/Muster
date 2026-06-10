using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddShopOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ShopOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GuildId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    ListingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OfferId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BuyerId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    SellerId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    ItemNameSnapshot = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    CurrencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    FeeAmount = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StatusChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SettledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DisputedBy = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    DisputeReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DisputeEvidenceKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResolvedBy = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    RatingWindowClosesAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopOrders_ShopListings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "ShopListings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShopOrders_GuildId_BuyerId",
                table: "ShopOrders",
                columns: new[] { "GuildId", "BuyerId" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopOrders_GuildId_SellerId",
                table: "ShopOrders",
                columns: new[] { "GuildId", "SellerId" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopOrders_GuildId_Status",
                table: "ShopOrders",
                columns: new[] { "GuildId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopOrders_ListingId",
                table: "ShopOrders",
                column: "ListingId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShopOrders");
        }
    }
}
