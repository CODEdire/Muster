using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestDisputeReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisputeReason",
                table: "Quests",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisputeReason",
                table: "Quests");
        }
    }
}
