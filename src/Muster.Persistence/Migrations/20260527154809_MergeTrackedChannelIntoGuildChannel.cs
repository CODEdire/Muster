using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MergeTrackedChannelIntoGuildChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DailyCapPoints",
                table: "GuildChannels",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "GuildChannels",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Guards",
                table: "GuildChannels",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MessageCooldownSeconds",
                table: "GuildChannels",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MessageDailyCapPoints",
                table: "GuildChannels",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MessagesPerPoint",
                table: "GuildChannels",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Mode",
                table: "GuildChannels",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PointsPerMessage",
                table: "GuildChannels",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PointsPerMinute",
                table: "GuildChannels",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_GuildChannels_GuildId_Mode",
                table: "GuildChannels",
                columns: new[] { "GuildId", "Mode" });

            // Move tracking config from the old per-rule table onto the merged roster row. The synced
            // GuildChannel.Kind is authoritative, so we don't touch it on the UPDATE path.
            migrationBuilder.Sql(@"
                UPDATE gc SET
                    gc.Mode = tc.Mode,
                    gc.PointsPerMinute = tc.PointsPerMinute,
                    gc.PointsPerMessage = tc.PointsPerMessage,
                    gc.MessagesPerPoint = tc.MessagesPerPoint,
                    gc.MessageCooldownSeconds = tc.MessageCooldownSeconds,
                    gc.MessageDailyCapPoints = tc.MessageDailyCapPoints,
                    gc.DailyCapPoints = tc.DailyCapPoints,
                    gc.Guards = tc.Guards
                FROM GuildChannels gc
                INNER JOIN TrackedChannels tc ON tc.GuildId = gc.GuildId AND tc.ChannelId = gc.ChannelId;");

            // Tracked channels the roster sync never captured: insert a row. Old TrackedChannelKind (Voice=0,
            // Text=1) maps to the flipped GuildChannelKind (Text=0, Voice=1).
            migrationBuilder.Sql(@"
                INSERT INTO GuildChannels
                    (GuildId, ChannelId, Name, Kind, Position, DeletedAt,
                     Mode, PointsPerMinute, PointsPerMessage, MessagesPerPoint, MessageCooldownSeconds, MessageDailyCapPoints, DailyCapPoints, Guards)
                SELECT tc.GuildId, tc.ChannelId, tc.ChannelName, CASE tc.Kind WHEN 0 THEN 1 ELSE 0 END, NULL, NULL,
                       tc.Mode, tc.PointsPerMinute, tc.PointsPerMessage, tc.MessagesPerPoint, tc.MessageCooldownSeconds, tc.MessageDailyCapPoints, tc.DailyCapPoints, tc.Guards
                FROM TrackedChannels tc
                WHERE NOT EXISTS (SELECT 1 FROM GuildChannels gc WHERE gc.GuildId = tc.GuildId AND gc.ChannelId = tc.ChannelId);");

            migrationBuilder.DropTable(
                name: "TrackedChannels");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GuildChannels_GuildId_Mode",
                table: "GuildChannels");

            migrationBuilder.DropColumn(
                name: "DailyCapPoints",
                table: "GuildChannels");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "GuildChannels");

            migrationBuilder.DropColumn(
                name: "Guards",
                table: "GuildChannels");

            migrationBuilder.DropColumn(
                name: "MessageCooldownSeconds",
                table: "GuildChannels");

            migrationBuilder.DropColumn(
                name: "MessageDailyCapPoints",
                table: "GuildChannels");

            migrationBuilder.DropColumn(
                name: "MessagesPerPoint",
                table: "GuildChannels");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "GuildChannels");

            migrationBuilder.DropColumn(
                name: "PointsPerMessage",
                table: "GuildChannels");

            migrationBuilder.DropColumn(
                name: "PointsPerMinute",
                table: "GuildChannels");

            migrationBuilder.CreateTable(
                name: "TrackedChannels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    ChannelName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DailyCapPoints = table.Column<int>(type: "int", nullable: false),
                    Guards = table.Column<int>(type: "int", nullable: false),
                    GuildId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    MessageCooldownSeconds = table.Column<int>(type: "int", nullable: false),
                    MessageDailyCapPoints = table.Column<int>(type: "int", nullable: false),
                    MessagesPerPoint = table.Column<int>(type: "int", nullable: false),
                    Mode = table.Column<int>(type: "int", nullable: false),
                    PointsPerMessage = table.Column<int>(type: "int", nullable: false),
                    PointsPerMinute = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackedChannels", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrackedChannels_GuildId_ChannelId",
                table: "TrackedChannels",
                columns: new[] { "GuildId", "ChannelId" },
                unique: true);
        }
    }
}
