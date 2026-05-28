using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P8BonusConditions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MinMinutes",
                table: "RewardMultipliers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MinPeopleInChannel",
                table: "RewardMultipliers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OccupiedSince",
                table: "GuildChannels",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MinMinutes",
                table: "RewardMultipliers");

            migrationBuilder.DropColumn(
                name: "MinPeopleInChannel",
                table: "RewardMultipliers");

            migrationBuilder.DropColumn(
                name: "OccupiedSince",
                table: "GuildChannels");
        }
    }
}
