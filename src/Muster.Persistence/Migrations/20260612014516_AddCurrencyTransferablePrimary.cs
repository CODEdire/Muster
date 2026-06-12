using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCurrencyTransferablePrimary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPrimary",
                table: "Currencies",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTransferable",
                table: "Currencies",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Backfill sensible defaults for existing guilds: spendable currencies become transferable, and each
            // guild's first spendable currency (by code) becomes the default/primary. Non-spendable score
            // currencies (e.g. POINTS) stay non-transferable and are never auto-primary.
            migrationBuilder.Sql("UPDATE [Currencies] SET [IsTransferable] = 1 WHERE [IsSpendable] = 1;");
            migrationBuilder.Sql(@"
                WITH ranked AS (
                    SELECT [Id], ROW_NUMBER() OVER (PARTITION BY [GuildId] ORDER BY [Code]) AS rn
                    FROM [Currencies] WHERE [IsSpendable] = 1)
                UPDATE c SET c.[IsPrimary] = 1
                FROM [Currencies] c JOIN ranked r ON c.[Id] = r.[Id]
                WHERE r.rn = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPrimary",
                table: "Currencies");

            migrationBuilder.DropColumn(
                name: "IsTransferable",
                table: "Currencies");
        }
    }
}
