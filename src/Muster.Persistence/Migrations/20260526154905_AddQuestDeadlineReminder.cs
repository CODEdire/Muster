using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestDeadlineReminder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeadlineReminderSentAt",
                table: "Quests",
                type: "datetimeoffset",
                nullable: true);

            // Seed DeadlineReminderHours (default 24) into the Quests settings JSON for guilds serialized before it
            // existed, so EF's partial JSON_MODIFY update can find '$.Quests.DeadlineReminderHours'.
            migrationBuilder.Sql(@"
UPDATE [Guilds]
SET [Settings] = JSON_MODIFY([Settings], '$.Quests.DeadlineReminderHours', CONVERT(int, 24))
WHERE ISJSON([Settings]) = 1
  AND JSON_QUERY([Settings], '$.Quests') IS NOT NULL
  AND JSON_VALUE([Settings], '$.Quests.DeadlineReminderHours') IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeadlineReminderSentAt",
                table: "Quests");
        }
    }
}
