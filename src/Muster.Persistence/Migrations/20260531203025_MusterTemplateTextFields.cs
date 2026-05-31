using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MusterTemplateTextFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Emojis",
                table: "ReactionMusters");

            migrationBuilder.DropColumn(
                name: "Emoji",
                table: "MusterTemplates");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "MusterTemplates");

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "MusterTemplates",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Prompt",
                table: "MusterTemplates",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "MusterTemplates",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Prompt",
                table: "MusterTemplates");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "MusterTemplates");

            migrationBuilder.AddColumn<string>(
                name: "Emojis",
                table: "ReactionMusters",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "MusterTemplates",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(300)",
                oldMaxLength: 300,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Emoji",
                table: "MusterTemplates",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "MusterTemplates",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }
    }
}
