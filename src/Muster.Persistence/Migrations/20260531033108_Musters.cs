using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Musters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Emoji",
                table: "ReactionParticipants");

            migrationBuilder.RenameColumn(
                name: "ReactedAt",
                table: "ReactionParticipants",
                newName: "CheckedInAt");

            // MessageId is gone (the Discord message now lives in PostedMessage); CreatedBy is a new, unrelated
            // column — drop + add rather than the auto-scaffolded rename, which would carry message ids into the
            // creator field.
            migrationBuilder.DropColumn(
                name: "MessageId",
                table: "ReactionMusters");

            migrationBuilder.AddColumn<decimal>(
                name: "CreatedBy",
                table: "ReactionMusters",
                type: "decimal(20,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "CoinGate",
                table: "TrackingSessions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "ReactionParticipants",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClosedAt",
                table: "ReactionMusters",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "ReactionMusters",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "ReactionMusters",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "ReactionMusters",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MusterSessionLinks",
                columns: table => new
                {
                    MusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusterSessionLinks", x => new { x.MusterId, x.SessionId });
                    table.ForeignKey(
                        name: "FK_MusterSessionLinks_ReactionMusters_MusterId",
                        column: x => x.MusterId,
                        principalTable: "ReactionMusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrackingSessions_GuildId_ScheduledEventId",
                table: "TrackingSessions",
                columns: new[] { "GuildId", "ScheduledEventId" },
                unique: true,
                filter: "[Status] = 0 AND [ScheduledEventId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReactionMusters_GuildId_Status",
                table: "ReactionMusters",
                columns: new[] { "GuildId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MusterSessionLinks_SessionId",
                table: "MusterSessionLinks",
                column: "SessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MusterSessionLinks");

            migrationBuilder.DropIndex(
                name: "IX_TrackingSessions_GuildId_ScheduledEventId",
                table: "TrackingSessions");

            migrationBuilder.DropIndex(
                name: "IX_ReactionMusters_GuildId_Status",
                table: "ReactionMusters");

            migrationBuilder.DropColumn(
                name: "CoinGate",
                table: "TrackingSessions");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "ReactionParticipants");

            migrationBuilder.DropColumn(
                name: "ClosedAt",
                table: "ReactionMusters");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "ReactionMusters");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "ReactionMusters");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "ReactionMusters");

            migrationBuilder.RenameColumn(
                name: "CheckedInAt",
                table: "ReactionParticipants",
                newName: "ReactedAt");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "ReactionMusters");

            migrationBuilder.AddColumn<decimal>(
                name: "MessageId",
                table: "ReactionMusters",
                type: "decimal(20,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Emoji",
                table: "ReactionParticipants",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }
    }
}
