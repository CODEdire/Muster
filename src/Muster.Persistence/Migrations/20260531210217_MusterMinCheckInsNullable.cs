using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MusterMinCheckInsNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "MinCheckIns",
                table: "ReactionMusters",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<int>(
                name: "MinCheckIns",
                table: "MusterTemplates",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<int>(
                name: "DefaultMinCheckIns",
                table: "GuildMusterSettings",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            // The old non-nullable default was 0 ("no minimum"). Now null carries that meaning and 0 is a real
            // (always-met) minimum — convert pre-existing 0s to null so they read as "No minimum".
            migrationBuilder.Sql("UPDATE [ReactionMusters] SET [MinCheckIns] = NULL WHERE [MinCheckIns] = 0;");
            migrationBuilder.Sql("UPDATE [MusterTemplates] SET [MinCheckIns] = NULL WHERE [MinCheckIns] = 0;");
            migrationBuilder.Sql("UPDATE [GuildMusterSettings] SET [DefaultMinCheckIns] = NULL WHERE [DefaultMinCheckIns] = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "MinCheckIns",
                table: "ReactionMusters",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "MinCheckIns",
                table: "MusterTemplates",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "DefaultMinCheckIns",
                table: "GuildMusterSettings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);
        }
    }
}
