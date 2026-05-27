using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionTrackingChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackgroundVoicePresences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GuildId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    UserId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    ChannelId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    OpenSegmentStart = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CarrySeconds = table.Column<int>(type: "int", nullable: false),
                    AwardedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AwardedPointsToday = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundVoicePresences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrackedChannels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GuildId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    ChannelId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Mode = table.Column<int>(type: "int", nullable: false),
                    PointsPerMinute = table.Column<int>(type: "int", nullable: false),
                    PointsPerMessage = table.Column<int>(type: "int", nullable: false),
                    DailyCapPoints = table.Column<int>(type: "int", nullable: false),
                    RequireUnmuted = table.Column<bool>(type: "bit", nullable: false),
                    RequireNotAlone = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackedChannels", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundVoicePresences_GuildId_UserId_ChannelId",
                table: "BackgroundVoicePresences",
                columns: new[] { "GuildId", "UserId", "ChannelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrackedChannels_GuildId_ChannelId",
                table: "TrackedChannels",
                columns: new[] { "GuildId", "ChannelId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackgroundVoicePresences");

            migrationBuilder.DropTable(
                name: "TrackedChannels");
        }
    }
}
