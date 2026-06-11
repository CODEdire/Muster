using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Muster.Domain.Entities.Guilds;
using Muster.Persistence;

namespace Muster.Infrastructure.Services.Quests;

/// <summary>
/// Reads and writes a guild's <see cref="GuildQuestSettings"/> row. When a guild has no row yet, callers get a
/// (non-persisted) instance forward-migrated from the legacy owned <see cref="QuestSettings"/> JSON — or, for an
/// unknown guild, from <c>IOptions&lt;GuildQuestSettings&gt;</c> (bound under <c>GuildDefaults:Quests</c>). The same
/// derivation seeds the row the first time it's written, so a guild's existing legacy quest config is preserved on
/// cutover even if the deploy backfill hasn't run yet. Table-per-feature + configured-bootstrap pattern (mirrors
/// <see cref="Musters.GuildMusterSettingsService"/> / <c>GuildShopSettingsService</c>).
/// </summary>
public class GuildQuestSettingsService(MusterDbContext db, IOptions<GuildQuestSettings> defaults)
{
    /// <summary>The guild's row, or a (non-persisted) instance derived from the legacy settings / platform defaults
    /// when none exists. Never writes — the deploy backfill and the first <see cref="UpsertAsync"/> persist it.</summary>
    public async Task<GuildQuestSettings> GetAsync(ulong guildId, CancellationToken ct = default)
        => await db.GuildQuestSettings.FindAsync([guildId], ct) ?? await DeriveAsync(guildId, ct);

    /// <summary>Load the guild's row (seeding it from the legacy settings / defaults if missing), apply
    /// <paramref name="mutate"/>, save.</summary>
    public async Task<GuildQuestSettings> UpsertAsync(ulong guildId, Action<GuildQuestSettings> mutate, CancellationToken ct = default)
    {
        var row = await db.GuildQuestSettings.FindAsync([guildId], ct);
        if (row is null)
        {
            row = await DeriveAsync(guildId, ct);
            db.GuildQuestSettings.Add(row);
        }

        mutate(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>A non-persisted settings instance for a guild with no row: forward-migrated from the legacy owned
    /// <see cref="QuestSettings"/> when the guild exists, else the configured platform defaults.</summary>
    private async Task<GuildQuestSettings> DeriveAsync(ulong guildId, CancellationToken ct)
    {
        var settings = await db.Guilds.AsNoTracking()
            .Where(g => g.Id == guildId).Select(g => g.Settings).FirstOrDefaultAsync(ct);
        return settings is not null ? GuildQuestSettings.FromLegacy(guildId, settings.Quests) : Defaults(guildId);
    }

    /// <summary>A new settings instance seeded from the configured platform defaults (GuildId stamped on).</summary>
    private GuildQuestSettings Defaults(ulong guildId)
    {
        var d = defaults.Value;
        return new GuildQuestSettings
        {
            GuildId = guildId,
            QuestsEnabled = d.QuestsEnabled,
            QuestChannelId = d.QuestChannelId,
            QuestModChannelId = d.QuestModChannelId,
            BoardRetentionHours = d.BoardRetentionHours,
            DeadlineReminderHours = d.DeadlineReminderHours,
            QuestsRequireApproval = d.QuestsRequireApproval,
            PersonalQuestIntakeApproval = d.PersonalQuestIntakeApproval,
            AllowSelfParticipation = d.AllowSelfParticipation,
            FinalApprovalMode = d.FinalApprovalMode,
            IntakeTimeoutHours = d.IntakeTimeoutHours,
            IntakeTimeoutAction = d.IntakeTimeoutAction,
            ClaimTimeoutHours = d.ClaimTimeoutHours,
            SubmissionTimeoutHours = d.SubmissionTimeoutHours,
            SubmissionTimeoutAction = d.SubmissionTimeoutAction,
            FinalApprovalTimeoutHours = d.FinalApprovalTimeoutHours,
            FinalApprovalTimeoutAction = d.FinalApprovalTimeoutAction,
            DisputeTimeoutHours = d.DisputeTimeoutHours,
            MaxOpenQuestsPerPoster = d.MaxOpenQuestsPerPoster,
            MaxActiveClaimsPerUser = d.MaxActiveClaimsPerUser,
            MaxRevisions = d.MaxRevisions,
            TierSPoints = d.TierSPoints,
            TierAPoints = d.TierAPoints,
            TierBPoints = d.TierBPoints,
            TierCPoints = d.TierCPoints,
            TierDPoints = d.TierDPoints,
            TierEPoints = d.TierEPoints,
        };
    }

    /// <summary>Deploy-time all-guilds backfill: insert a <see cref="GuildQuestSettings"/> row (forward-migrated from
    /// the legacy owned <see cref="QuestSettings"/>) for every guild lacking one. Idempotent — re-running adds nothing.
    /// Returns the number of rows inserted. Called from <c>Muster.MigrationService</c> after the EF migration applies.</summary>
    public static async Task<int> BackfillAsync(MusterDbContext db, CancellationToken ct = default)
    {
        var existing = (await db.GuildQuestSettings.Select(x => x.GuildId).ToListAsync(ct)).ToHashSet();
        var guilds = await db.Guilds.AsNoTracking().ToListAsync(ct);

        var added = 0;
        foreach (var g in guilds)
        {
            if (existing.Contains(g.Id))
            {
                continue;
            }

            db.GuildQuestSettings.Add(GuildQuestSettings.FromLegacy(g.Id, g.Settings.Quests));
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return added;
    }
}
