using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRatings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RatingsClosed",
                table: "ShopOrders",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Ratings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GuildId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    Context = table.Column<int>(type: "int", nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RaterId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    SubjectId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    Stars = table.Column<int>(type: "int", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Hidden = table.Column<bool>(type: "bit", nullable: false),
                    Moderated = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevealedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ratings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Ratings_Context_SourceId",
                table: "Ratings",
                columns: new[] { "Context", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_Ratings_Context_SourceId_RaterId",
                table: "Ratings",
                columns: new[] { "Context", "SourceId", "RaterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ratings_GuildId_Context_SubjectId_Role",
                table: "Ratings",
                columns: new[] { "GuildId", "Context", "SubjectId", "Role" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Ratings");

            migrationBuilder.DropColumn(
                name: "RatingsClosed",
                table: "ShopOrders");
        }
    }
}
