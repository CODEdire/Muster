using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "QuestTypeId",
                table: "Quests",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "QuestTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GuildId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Sort = table.Column<int>(type: "int", nullable: false),
                    Icon = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuestTypes_Guilds_GuildId",
                        column: x => x.GuildId,
                        principalTable: "Guilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Quests_QuestTypeId",
                table: "Quests",
                column: "QuestTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestTypes_GuildId_Name",
                table: "QuestTypes",
                columns: new[] { "GuildId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QuestTypes");

            migrationBuilder.DropIndex(
                name: "IX_Quests_QuestTypeId",
                table: "Quests");

            migrationBuilder.DropColumn(
                name: "QuestTypeId",
                table: "Quests");
        }
    }
}
