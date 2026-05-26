using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackfillBoardRetentionHours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Seed BoardRetentionHours (default 48) into the Quests settings JSON for guilds serialized before it
            // existed, so EF's partial JSON_MODIFY update can find '$.Quests.BoardRetentionHours'.
            migrationBuilder.Sql(@"
UPDATE [Guilds]
SET [Settings] = JSON_MODIFY([Settings], '$.Quests.BoardRetentionHours', CONVERT(int, 48))
WHERE ISJSON([Settings]) = 1
  AND JSON_QUERY([Settings], '$.Quests') IS NOT NULL
  AND JSON_VALUE([Settings], '$.Quests.BoardRetentionHours') IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
