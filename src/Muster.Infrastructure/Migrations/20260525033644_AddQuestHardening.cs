using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Missions",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StatusChangedAt",
                table: "Missions",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClaimedAt",
                table: "MissionParticipants",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "MissionParticipants",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewNote",
                table: "MissionParticipants",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RevisionCount",
                table: "MissionParticipants",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Missions");

            migrationBuilder.DropColumn(
                name: "StatusChangedAt",
                table: "Missions");

            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "MissionParticipants");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "MissionParticipants");

            migrationBuilder.DropColumn(
                name: "ReviewNote",
                table: "MissionParticipants");

            migrationBuilder.DropColumn(
                name: "RevisionCount",
                table: "MissionParticipants");
        }
    }
}
