using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditTargetOutcomeCorrelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "AuditLogs",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Outcome",
                table: "AuditLogs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "TargetUserId",
                table: "AuditLogs",
                type: "decimal(20,0)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_GuildId_CorrelationId",
                table: "AuditLogs",
                columns: new[] { "GuildId", "CorrelationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_GuildId_OccurredAt",
                table: "AuditLogs",
                columns: new[] { "GuildId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_GuildId_TargetUserId",
                table: "AuditLogs",
                columns: new[] { "GuildId", "TargetUserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_GuildId_CorrelationId",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_GuildId_OccurredAt",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_GuildId_TargetUserId",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "TargetUserId",
                table: "AuditLogs");
        }
    }
}
