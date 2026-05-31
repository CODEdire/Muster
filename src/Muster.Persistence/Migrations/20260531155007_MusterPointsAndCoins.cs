using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MusterPointsAndCoins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrencyId",
                table: "ReactionMusters");

            migrationBuilder.RenameColumn(
                name: "RewardAmount",
                table: "ReactionMusters",
                newName: "Points");

            migrationBuilder.AddColumn<Guid>(
                name: "CoinCurrencyId",
                table: "ReactionMusters",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Coins",
                table: "ReactionMusters",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "RetentionHours",
                table: "ReactionMusters",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoinCurrencyId",
                table: "ReactionMusters");

            migrationBuilder.DropColumn(
                name: "Coins",
                table: "ReactionMusters");

            migrationBuilder.DropColumn(
                name: "RetentionHours",
                table: "ReactionMusters");

            migrationBuilder.RenameColumn(
                name: "Points",
                table: "ReactionMusters",
                newName: "RewardAmount");

            migrationBuilder.AddColumn<Guid>(
                name: "CurrencyId",
                table: "ReactionMusters",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));
        }
    }
}
