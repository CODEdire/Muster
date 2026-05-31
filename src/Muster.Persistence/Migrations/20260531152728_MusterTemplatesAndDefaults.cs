using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MusterTemplatesAndDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AutoCreateGate",
                table: "GuildMusterSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "DefaultCoinCurrencyId",
                table: "GuildMusterSettings",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DefaultCoins",
                table: "GuildMusterSettings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "DefaultPoints",
                table: "GuildMusterSettings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "MusterTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GuildId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Emoji = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Points = table.Column<long>(type: "bigint", nullable: false),
                    Coins = table.Column<long>(type: "bigint", nullable: false),
                    CoinCurrencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RetentionHours = table.Column<int>(type: "int", nullable: false),
                    Capacity = table.Column<int>(type: "int", nullable: true),
                    ExpiryHours = table.Column<int>(type: "int", nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusterTemplates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MusterTemplates_GuildId_Enabled",
                table: "MusterTemplates",
                columns: new[] { "GuildId", "Enabled" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MusterTemplates");

            migrationBuilder.DropColumn(
                name: "AutoCreateGate",
                table: "GuildMusterSettings");

            migrationBuilder.DropColumn(
                name: "DefaultCoinCurrencyId",
                table: "GuildMusterSettings");

            migrationBuilder.DropColumn(
                name: "DefaultCoins",
                table: "GuildMusterSettings");

            migrationBuilder.DropColumn(
                name: "DefaultPoints",
                table: "GuildMusterSettings");
        }
    }
}
