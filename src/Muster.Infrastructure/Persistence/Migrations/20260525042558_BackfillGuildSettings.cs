using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muster.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackfillGuildSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill the JSON-owned Guild.Settings document with keys added after a guild row was first
            // written. EF Core updates a JSON column with per-property JSON_MODIFY, which fails on rows
            // whose document predates a key ("property cannot be found on the specified JSON path"). Each
            // statement adds a single key only where it's currently absent, so existing values are never
            // overwritten (and re-running is a no-op).
            void AddKey(string key, string jsonValue) => migrationBuilder.Sql(
                $"UPDATE [Guilds] SET [Settings] = JSON_MODIFY(COALESCE([Settings], '{{}}'), '$.{key}', {jsonValue}) " +
                $"WHERE JSON_VALUE([Settings], '$.{key}') IS NULL;");

            AddKey("PersonalQuestIntakeApproval", "CONVERT(bit, 1)");
            AddKey("FinalApprovalMode", "1");      // OwnerChoice
            AddKey("IntakeTimeoutHours", "0");
            AddKey("IntakeTimeoutAction", "0");    // Decline
            AddKey("ClaimTimeoutHours", "0");
            AddKey("SubmissionTimeoutHours", "0");
            AddKey("SubmissionTimeoutAction", "0"); // Approve
            AddKey("FinalApprovalTimeoutHours", "0");
            AddKey("FinalApprovalTimeoutAction", "0"); // Approve
            AddKey("MaxOpenQuestsPerPoster", "0");
            AddKey("MaxActiveClaimsPerUser", "0");
            AddKey("MaxRevisions", "0");
            AddKey("TierSPoints", "100");
            AddKey("TierAPoints", "75");
            AddKey("TierBPoints", "50");
            AddKey("TierCPoints", "30");
            AddKey("TierDPoints", "15");
            AddKey("TierEPoints", "5");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
