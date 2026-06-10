using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddShopOrderStoreId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "StoreId",
                table: "ShopOrders",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Best-effort backfill for orders placed before the column existed: take the store from the order's
            // listing where it still exists. Orders whose listing was already deleted keep the empty default
            // (they simply won't appear under a per-store filter).
            migrationBuilder.Sql(
                "UPDATE o SET o.StoreId = l.StoreId " +
                "FROM ShopOrders o INNER JOIN ShopListings l ON o.ListingId = l.Id " +
                "WHERE o.StoreId = '00000000-0000-0000-0000-000000000000';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StoreId",
                table: "ShopOrders");
        }
    }
}
