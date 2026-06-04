using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MusterResolveMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ResolveMode",
                table: "ReactionMusters",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ResolveMode",
                table: "MusterTemplates",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DefaultResolveMode",
                table: "GuildMusterSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResolveMode",
                table: "ReactionMusters");

            migrationBuilder.DropColumn(
                name: "ResolveMode",
                table: "MusterTemplates");

            migrationBuilder.DropColumn(
                name: "DefaultResolveMode",
                table: "GuildMusterSettings");
        }
    }
}
