using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameLedgerDropManualAwards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ManualAwards was a write-only audit duplicate of the ledger — drop it (actor lives in the audit trail).
            migrationBuilder.DropTable(name: "ManualAwards");

            // Rename the journal in place (preserve data): LedgerEntries -> CurrencyLedgerEntries, with its indexes + PK.
            migrationBuilder.RenameTable(name: "LedgerEntries", newName: "CurrencyLedgerEntries");

            migrationBuilder.RenameIndex(
                name: "IX_LedgerEntries_GuildId_UserId_CurrencyId_SeasonId",
                newName: "IX_CurrencyLedgerEntries_GuildId_UserId_CurrencyId_SeasonId",
                table: "CurrencyLedgerEntries");

            migrationBuilder.RenameIndex(
                name: "IX_LedgerEntries_SourceType_SourceId",
                newName: "IX_CurrencyLedgerEntries_SourceType_SourceId",
                table: "CurrencyLedgerEntries");

            migrationBuilder.Sql("EXEC sp_rename N'PK_LedgerEntries', N'PK_CurrencyLedgerEntries';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("EXEC sp_rename N'PK_CurrencyLedgerEntries', N'PK_LedgerEntries';");

            migrationBuilder.RenameIndex(
                name: "IX_CurrencyLedgerEntries_SourceType_SourceId",
                newName: "IX_LedgerEntries_SourceType_SourceId",
                table: "CurrencyLedgerEntries");

            migrationBuilder.RenameIndex(
                name: "IX_CurrencyLedgerEntries_GuildId_UserId_CurrencyId_SeasonId",
                newName: "IX_LedgerEntries_GuildId_UserId_CurrencyId_SeasonId",
                table: "CurrencyLedgerEntries");

            migrationBuilder.RenameTable(name: "CurrencyLedgerEntries", newName: "LedgerEntries");

            migrationBuilder.CreateTable(
                name: "ManualAwards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    AwardedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AwardedBy = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    CurrencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GuildId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UserId = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualAwards", x => x.Id);
                });
        }
    }
}
