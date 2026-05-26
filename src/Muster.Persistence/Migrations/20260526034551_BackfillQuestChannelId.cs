using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackfillQuestChannelId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // QuestChannelId is a new property inside the Quests settings JSON. Guilds serialized before it existed
            // have no '$.Quests.QuestChannelId' path, so EF's partial JSON_MODIFY update fails with
            // "Property cannot be found on the specified JSON path". Seed it (0 = board disabled) so the path
            // exists and subsequent updates succeed. New guilds serialize the property from the start.
            migrationBuilder.Sql(@"
UPDATE [Guilds]
SET [Settings] = JSON_MODIFY([Settings], '$.Quests.QuestChannelId', CONVERT(bigint, 0))
WHERE ISJSON([Settings]) = 1
  AND JSON_QUERY([Settings], '$.Quests') IS NOT NULL
  AND JSON_VALUE([Settings], '$.Quests.QuestChannelId') IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
