using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P2ActiveTimeSeasonsOptOut : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Tracking",
                table: "GuildMembers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ActiveCarrySeconds",
                table: "BackgroundVoicePresences",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ActiveOpenSegmentStart",
                table: "BackgroundVoicePresences",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SeasonParticipations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GuildId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    UserId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    SeasonId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VoiceMinutes = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeasonParticipations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SeasonParticipations_GuildId_UserId_SeasonId",
                table: "SeasonParticipations",
                columns: new[] { "GuildId", "UserId", "SeasonId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeasonParticipations");

            migrationBuilder.DropColumn(
                name: "Tracking",
                table: "GuildMembers");

            migrationBuilder.DropColumn(
                name: "ActiveCarrySeconds",
                table: "BackgroundVoicePresences");

            migrationBuilder.DropColumn(
                name: "ActiveOpenSegmentStart",
                table: "BackgroundVoicePresences");
        }
    }
}
