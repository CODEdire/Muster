using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddShopFeatured : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FeaturedUntil",
                table: "ShopListings",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FeaturedDurationHours",
                table: "GuildShopSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "FeaturedListingFee",
                table: "GuildShopSettings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "MaxFeaturedPerStore",
                table: "GuildShopSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FeaturedUntil",
                table: "ShopListings");

            migrationBuilder.DropColumn(
                name: "FeaturedDurationHours",
                table: "GuildShopSettings");

            migrationBuilder.DropColumn(
                name: "FeaturedListingFee",
                table: "GuildShopSettings");

            migrationBuilder.DropColumn(
                name: "MaxFeaturedPerStore",
                table: "GuildShopSettings");
        }
    }
}
