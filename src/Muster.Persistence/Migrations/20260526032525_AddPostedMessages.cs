using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPostedMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PostedMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GuildId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    MessageId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostedMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PostedMessages_EntityType_EntityId",
                table: "PostedMessages",
                columns: new[] { "EntityType", "EntityId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PostedMessages");
        }
    }
}
