using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDisputeTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DisputedBy",
                table: "Quests",
                type: "decimal(20,0)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisputedBy",
                table: "Quests");
        }
    }
}
