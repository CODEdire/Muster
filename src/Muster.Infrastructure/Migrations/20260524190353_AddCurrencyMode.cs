using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCurrencyMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Mode",
                table: "Currencies",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Mode",
                table: "Currencies");
        }
    }
}
