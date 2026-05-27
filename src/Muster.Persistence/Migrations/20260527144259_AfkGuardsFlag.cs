using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AfkGuardsFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Guards", table: "TrackingSessions", type: "int", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<int>(
                name: "Guards", table: "TrackedChannels", type: "int", nullable: false, defaultValue: 0);

            // Fold the three bool guards into the flags value (Unmuted=1, Undeafened=2, NotAlone=4).
            const string fold =
                "SET [Guards] = (CASE WHEN [RequireUnmuted] = 1 THEN 1 ELSE 0 END) " +
                "+ (CASE WHEN [RequireUndeafened] = 1 THEN 2 ELSE 0 END) " +
                "+ (CASE WHEN [RequireNotAlone] = 1 THEN 4 ELSE 0 END)";
            migrationBuilder.Sql($"UPDATE [TrackingSessions] {fold}");
            migrationBuilder.Sql($"UPDATE [TrackedChannels] {fold}");

            migrationBuilder.DropColumn(name: "RequireNotAlone", table: "TrackingSessions");
            migrationBuilder.DropColumn(name: "RequireUndeafened", table: "TrackingSessions");
            migrationBuilder.DropColumn(name: "RequireUnmuted", table: "TrackingSessions");
            migrationBuilder.DropColumn(name: "RequireNotAlone", table: "TrackedChannels");
            migrationBuilder.DropColumn(name: "RequireUndeafened", table: "TrackedChannels");
            migrationBuilder.DropColumn(name: "RequireUnmuted", table: "TrackedChannels");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "TrackingSessions", "TrackedChannels" })
            {
                migrationBuilder.AddColumn<bool>(name: "RequireUnmuted", table: table, type: "bit", nullable: false, defaultValue: false);
                migrationBuilder.AddColumn<bool>(name: "RequireUndeafened", table: table, type: "bit", nullable: false, defaultValue: false);
                migrationBuilder.AddColumn<bool>(name: "RequireNotAlone", table: table, type: "bit", nullable: false, defaultValue: false);
                migrationBuilder.Sql(
                    $"UPDATE [{table}] SET " +
                    "[RequireUnmuted] = CASE WHEN ([Guards] & 1) = 1 THEN 1 ELSE 0 END, " +
                    "[RequireUndeafened] = CASE WHEN ([Guards] & 2) = 2 THEN 1 ELSE 0 END, " +
                    "[RequireNotAlone] = CASE WHEN ([Guards] & 4) = 4 THEN 1 ELSE 0 END");
                migrationBuilder.DropColumn(name: "Guards", table: table);
            }
        }
    }
}
