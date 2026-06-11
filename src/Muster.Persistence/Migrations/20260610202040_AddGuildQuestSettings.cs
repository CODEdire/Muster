using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGuildQuestSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GuildQuestSettings",
                columns: table => new
                {
                    GuildId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    QuestsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    QuestChannelId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    QuestModChannelId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    BoardRetentionHours = table.Column<int>(type: "int", nullable: false),
                    DeadlineReminderHours = table.Column<int>(type: "int", nullable: false),
                    QuestsRequireApproval = table.Column<bool>(type: "bit", nullable: false),
                    PersonalQuestIntakeApproval = table.Column<bool>(type: "bit", nullable: false),
                    AllowSelfParticipation = table.Column<bool>(type: "bit", nullable: false),
                    FinalApprovalMode = table.Column<int>(type: "int", nullable: false),
                    IntakeTimeoutHours = table.Column<int>(type: "int", nullable: false),
                    IntakeTimeoutAction = table.Column<int>(type: "int", nullable: false),
                    ClaimTimeoutHours = table.Column<int>(type: "int", nullable: false),
                    SubmissionTimeoutHours = table.Column<int>(type: "int", nullable: false),
                    SubmissionTimeoutAction = table.Column<int>(type: "int", nullable: false),
                    FinalApprovalTimeoutHours = table.Column<int>(type: "int", nullable: false),
                    FinalApprovalTimeoutAction = table.Column<int>(type: "int", nullable: false),
                    DisputeTimeoutHours = table.Column<int>(type: "int", nullable: false),
                    MaxOpenQuestsPerPoster = table.Column<int>(type: "int", nullable: false),
                    MaxActiveClaimsPerUser = table.Column<int>(type: "int", nullable: false),
                    MaxRevisions = table.Column<int>(type: "int", nullable: false),
                    TierSPoints = table.Column<long>(type: "bigint", nullable: false),
                    TierAPoints = table.Column<long>(type: "bigint", nullable: false),
                    TierBPoints = table.Column<long>(type: "bigint", nullable: false),
                    TierCPoints = table.Column<long>(type: "bigint", nullable: false),
                    TierDPoints = table.Column<long>(type: "bigint", nullable: false),
                    TierEPoints = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildQuestSettings", x => x.GuildId);
                    table.ForeignKey(
                        name: "FK_GuildQuestSettings_Guilds_GuildId",
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
                name: "GuildQuestSettings");
        }
    }
}
