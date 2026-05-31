using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Musters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Emoji",
                table: "ReactionParticipants");

            migrationBuilder.DropColumn(
                name: "CurrencyId",
                table: "ReactionMusters");

            migrationBuilder.DropColumn(
                name: "Emojis",
                table: "ReactionMusters");

            migrationBuilder.RenameColumn(
                name: "ReactedAt",
                table: "ReactionParticipants",
                newName: "CheckedInAt");

            migrationBuilder.RenameColumn(
                name: "RewardAmount",
                table: "ReactionMusters",
                newName: "Points");

            // NOT a rename: MessageId is a Discord message snowflake, CreatedBy is a user id. EF auto-detects a
            // rename (same CLR type), which would carry the message id into CreatedBy — drop + add instead.
            migrationBuilder.DropColumn(
                name: "MessageId",
                table: "ReactionMusters");

            migrationBuilder.AddColumn<decimal>(
                name: "CreatedBy",
                table: "ReactionMusters",
                type: "decimal(20,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "CoinGate",
                table: "TrackingSessions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "ReactionParticipants",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClosedAt",
                table: "ReactionMusters",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CoinCurrencyId",
                table: "ReactionMusters",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Coins",
                table: "ReactionMusters",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "ReactionMusters",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<int>(
                name: "MinCheckIns",
                table: "ReactionMusters",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetentionHours",
                table: "ReactionMusters",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "ReactionMusters",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "ReactionMusters",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GuildMusterSettings",
                columns: table => new
                {
                    GuildId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    MusterChannelId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    BoardRetentionHours = table.Column<int>(type: "int", nullable: false),
                    AutoCreateOnSession = table.Column<bool>(type: "bit", nullable: false),
                    CreatorAutoCheckIn = table.Column<bool>(type: "bit", nullable: false),
                    DefaultExpiryHours = table.Column<int>(type: "int", nullable: false),
                    AutoCreateGate = table.Column<int>(type: "int", nullable: false),
                    AutoCreateChannel = table.Column<int>(type: "int", nullable: false),
                    DefaultPoints = table.Column<long>(type: "bigint", nullable: false),
                    DefaultCoins = table.Column<long>(type: "bigint", nullable: false),
                    DefaultMinCheckIns = table.Column<int>(type: "int", nullable: true),
                    DefaultCoinCurrencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
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

            migrationBuilder.CreateTable(
                name: "MusterSessionLinks",
                columns: table => new
                {
                    MusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusterSessionLinks", x => new { x.MusterId, x.SessionId });
                    table.ForeignKey(
                        name: "FK_MusterSessionLinks_ReactionMusters_MusterId",
                        column: x => x.MusterId,
                        principalTable: "ReactionMusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MusterTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GuildId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Title = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Prompt = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Points = table.Column<long>(type: "bigint", nullable: false),
                    Coins = table.Column<long>(type: "bigint", nullable: false),
                    CoinCurrencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RetentionHours = table.Column<int>(type: "int", nullable: false),
                    Capacity = table.Column<int>(type: "int", nullable: true),
                    MinCheckIns = table.Column<int>(type: "int", nullable: true),
                    ExpiryHours = table.Column<int>(type: "int", nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusterTemplates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrackingSessions_GuildId_ScheduledEventId",
                table: "TrackingSessions",
                columns: new[] { "GuildId", "ScheduledEventId" },
                unique: true,
                filter: "[Status] = 0 AND [ScheduledEventId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReactionMusters_GuildId_Status",
                table: "ReactionMusters",
                columns: new[] { "GuildId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MusterSessionLinks_SessionId",
                table: "MusterSessionLinks",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_MusterTemplates_GuildId_Enabled",
                table: "MusterTemplates",
                columns: new[] { "GuildId", "Enabled" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GuildMusterSettings");

            migrationBuilder.DropTable(
                name: "MusterSessionLinks");

            migrationBuilder.DropTable(
                name: "MusterTemplates");

            migrationBuilder.DropIndex(
                name: "IX_TrackingSessions_GuildId_ScheduledEventId",
                table: "TrackingSessions");

            migrationBuilder.DropIndex(
                name: "IX_ReactionMusters_GuildId_Status",
                table: "ReactionMusters");

            migrationBuilder.DropColumn(
                name: "CoinGate",
                table: "TrackingSessions");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "ReactionParticipants");

            migrationBuilder.DropColumn(
                name: "ClosedAt",
                table: "ReactionMusters");

            migrationBuilder.DropColumn(
                name: "CoinCurrencyId",
                table: "ReactionMusters");

            migrationBuilder.DropColumn(
                name: "Coins",
                table: "ReactionMusters");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "ReactionMusters");

            migrationBuilder.DropColumn(
                name: "MinCheckIns",
                table: "ReactionMusters");

            migrationBuilder.DropColumn(
                name: "RetentionHours",
                table: "ReactionMusters");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "ReactionMusters");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "ReactionMusters");

            migrationBuilder.RenameColumn(
                name: "CheckedInAt",
                table: "ReactionParticipants",
                newName: "ReactedAt");

            migrationBuilder.RenameColumn(
                name: "Points",
                table: "ReactionMusters",
                newName: "RewardAmount");

            // Mirror of the Up: drop + add, not a rename (MessageId ≠ CreatedBy semantically).
            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "ReactionMusters");

            migrationBuilder.AddColumn<decimal>(
                name: "MessageId",
                table: "ReactionMusters",
                type: "decimal(20,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Emoji",
                table: "ReactionParticipants",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "CurrencyId",
                table: "ReactionMusters",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "Emojis",
                table: "ReactionMusters",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }
    }
}
