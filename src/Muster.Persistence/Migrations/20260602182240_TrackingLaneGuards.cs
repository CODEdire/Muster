using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrackingLaneGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Split the single per-channel Guards column into three nullable per-lane overrides. Add the new
            // columns first, carry the existing value onto the Background lane (preserving current behavior),
            // then drop the old column. Session/Event lanes start null = inherit the guild default.
            migrationBuilder.AddColumn<int>(
                name: "BackgroundGuards",
                table: "GuildChannels",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EventGuards",
                table: "GuildChannels",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SessionGuards",
                table: "GuildChannels",
                type: "int",
                nullable: true);

            migrationBuilder.Sql("UPDATE [GuildChannels] SET [BackgroundGuards] = [Guards];");

            migrationBuilder.DropColumn(
                name: "Guards",
                table: "GuildChannels");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Guards",
                table: "GuildChannels",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Restore the single column from the Background lane (the lane the Up migration carried it onto).
            migrationBuilder.Sql("UPDATE [GuildChannels] SET [Guards] = ISNULL([BackgroundGuards], 0);");

            migrationBuilder.DropColumn(
                name: "BackgroundGuards",
                table: "GuildChannels");

            migrationBuilder.DropColumn(
                name: "EventGuards",
                table: "GuildChannels");

            migrationBuilder.DropColumn(
                name: "SessionGuards",
                table: "GuildChannels");
        }
    }
}
