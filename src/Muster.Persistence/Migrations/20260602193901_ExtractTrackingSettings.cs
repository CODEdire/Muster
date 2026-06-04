using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExtractTrackingSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GuildTrackingSettings",
                columns: table => new
                {
                    GuildId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    BackgroundTrackingOptIn = table.Column<bool>(type: "bit", nullable: false),
                    SessionCoinCurrencyCode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    MinutesPerCoin = table.Column<int>(type: "int", nullable: false),
                    PointsPerVoiceMinute = table.Column<int>(type: "int", nullable: false),
                    DefaultBackgroundGuards = table.Column<int>(type: "int", nullable: false),
                    DefaultSessionGuards = table.Column<int>(type: "int", nullable: false),
                    DefaultEventGuards = table.Column<int>(type: "int", nullable: false),
                    MaxSessionHours = table.Column<int>(type: "int", nullable: false),
                    ActivityRetentionDays = table.Column<int>(type: "int", nullable: false),
                    MinTrackedSeconds = table.Column<int>(type: "int", nullable: false),
                    MultiplierStacking = table.Column<int>(type: "int", nullable: false),
                    MultiplierCap = table.Column<decimal>(type: "decimal(6,3)", precision: 6, scale: 3, nullable: false),
                    SessionStartBonus = table.Column<int>(type: "int", nullable: false),
                    SessionEndBonus = table.Column<int>(type: "int", nullable: false),
                    StartBonusWindowMinutes = table.Column<int>(type: "int", nullable: false),
                    EndBonusWindowMinutes = table.Column<int>(type: "int", nullable: false),
                    MultiplyPresenceBonuses = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildTrackingSettings", x => x.GuildId);
                    table.ForeignKey(
                        name: "FK_GuildTrackingSettings_Guilds_GuildId",
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
                name: "GuildTrackingSettings");
        }
    }
}
