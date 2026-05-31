using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MusterMinCheckIns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MinCheckIns",
                table: "ReactionMusters",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MinCheckIns",
                table: "MusterTemplates",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DefaultMinCheckIns",
                table: "GuildMusterSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MinCheckIns",
                table: "ReactionMusters");

            migrationBuilder.DropColumn(
                name: "MinCheckIns",
                table: "MusterTemplates");

            migrationBuilder.DropColumn(
                name: "DefaultMinCheckIns",
                table: "GuildMusterSettings");
        }
    }
}
