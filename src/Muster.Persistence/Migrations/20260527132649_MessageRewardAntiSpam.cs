using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MessageRewardAntiSpam : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MessageCooldownSeconds",
                table: "TrackedChannels",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MessageDailyCapPoints",
                table: "TrackedChannels",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MessagesPerPoint",
                table: "TrackedChannels",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MessageRewardStates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GuildId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    UserId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    ChannelId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    MessagesSinceAward = table.Column<int>(type: "int", nullable: false),
                    LastRewardAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AwardedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AwardedPointsToday = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageRewardStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessageRewardStates_GuildId_UserId_ChannelId",
                table: "MessageRewardStates",
                columns: new[] { "GuildId", "UserId", "ChannelId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessageRewardStates");

            migrationBuilder.DropColumn(
                name: "MessageCooldownSeconds",
                table: "TrackedChannels");

            migrationBuilder.DropColumn(
                name: "MessageDailyCapPoints",
                table: "TrackedChannels");

            migrationBuilder.DropColumn(
                name: "MessagesPerPoint",
                table: "TrackedChannels");
        }
    }
}
