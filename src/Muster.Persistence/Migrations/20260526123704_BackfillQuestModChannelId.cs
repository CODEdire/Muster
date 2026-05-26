using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackfillQuestModChannelId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Seed QuestModChannelId (0 = none) into the Quests settings JSON for guilds serialized before it
            // existed, so EF's partial JSON_MODIFY update can find '$.Quests.QuestModChannelId'.
            migrationBuilder.Sql(@"
UPDATE [Guilds]
SET [Settings] = JSON_MODIFY([Settings], '$.Quests.QuestModChannelId', CONVERT(bigint, 0))
WHERE ISJSON([Settings]) = 1
  AND JSON_QUERY([Settings], '$.Quests') IS NOT NULL
  AND JSON_VALUE([Settings], '$.Quests.QuestModChannelId') IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
