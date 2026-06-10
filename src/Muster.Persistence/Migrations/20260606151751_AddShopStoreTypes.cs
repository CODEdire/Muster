using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddShopStoreTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "StoreTypeId",
                table: "ShopStores",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ShopStoreTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GuildId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Sort = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopStoreTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopStoreTypes_Guilds_GuildId",
                        column: x => x.GuildId,
                        principalTable: "Guilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShopStores_StoreTypeId",
                table: "ShopStores",
                column: "StoreTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopStoreTypes_GuildId_Name",
                table: "ShopStoreTypes",
                columns: new[] { "GuildId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShopStoreTypes");

            migrationBuilder.DropIndex(
                name: "IX_ShopStores_StoreTypeId",
                table: "ShopStores");

            migrationBuilder.DropColumn(
                name: "StoreTypeId",
                table: "ShopStores");
        }
    }
}
