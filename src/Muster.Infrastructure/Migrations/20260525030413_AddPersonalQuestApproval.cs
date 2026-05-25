using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonalQuestApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequiresFinalApproval",
                table: "Missions",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequiresFinalApproval",
                table: "Missions");
        }
    }
}
