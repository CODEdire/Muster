using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class QuestBoardOriginEscrow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "EscrowAmount",
                table: "Missions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "Origin",
                table: "Missions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "OwnerId",
                table: "Missions",
                type: "decimal(20,0)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EscrowAmount",
                table: "Missions");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "Missions");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Missions");
        }
    }
}
