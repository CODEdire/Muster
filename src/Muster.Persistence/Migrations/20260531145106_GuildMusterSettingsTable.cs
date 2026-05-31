using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GuildMusterSettingsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GuildMusterSettings",
                columns: table => new
                {
                    GuildId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    MusterChannelId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    BoardRetentionHours = table.Column<int>(type: "int", nullable: false),
                    AutoCreateOnSession = table.Column<bool>(type: "bit", nullable: false),
                    AllowedChannelIds = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildMusterSettings", x => x.GuildId);
                    table.ForeignKey(
                        name: "FK_GuildMusterSettings_Guilds_GuildId",
                        column: x => x.GuildId,
                        principalTable: "Guilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GuildMusterSettings");
        }
    }
}
