using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MusterCreatorCheckInAndExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CreatorAutoCheckIn",
                table: "GuildMusterSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "DefaultExpiryHours",
                table: "GuildMusterSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatorAutoCheckIn",
                table: "GuildMusterSettings");

            migrationBuilder.DropColumn(
                name: "DefaultExpiryHours",
                table: "GuildMusterSettings");
        }
    }
}
