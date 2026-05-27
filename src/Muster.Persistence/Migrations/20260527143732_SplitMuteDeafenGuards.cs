using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SplitMuteDeafenGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequireUndeafened",
                table: "TrackingSessions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequireUndeafened",
                table: "TrackedChannels",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Preserve prior behavior: the old single RequireUnmuted excluded muted *and* deafened, so existing
            // rows that excluded muted keep excluding deafened too. New rows use the split defaults.
            migrationBuilder.Sql("UPDATE [TrackingSessions] SET [RequireUndeafened] = [RequireUnmuted]");
            migrationBuilder.Sql("UPDATE [TrackedChannels] SET [RequireUndeafened] = [RequireUnmuted]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequireUndeafened",
                table: "TrackingSessions");

            migrationBuilder.DropColumn(
                name: "RequireUndeafened",
                table: "TrackedChannels");
        }
    }
}
