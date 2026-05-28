using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services.Platform;

/// <summary>
/// Ergonomic per-action wrappers around <see cref="AuditService.RecordAsync{T}"/>. Each helper pairs a typed
/// payload with the matching <see cref="AuditAction"/> so call sites read like the verb itself — and the
/// JSON shape stays in sync with the formatter on the other end.
/// </summary>
public static class AuditExtensions
{
    // ---- Configuration -----------------------------------------------------

    public static Task RecordRoleToggleAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        string kind, ulong roleId, string roleName, bool added,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Config.RoleByKind(kind),
            new RoleTogglePayload(kind, roleId, roleName, added),
            origin: origin, targetUserId: targetUserId, ct: ct);

    public static Task RecordLedgerRetentionAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        int oldDays, int newDays,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Config.LedgerRetention,
            new LedgerRetentionPayload(oldDays, newDays),
            origin: origin, targetUserId: targetUserId, ct: ct);

    public static Task RecordMultiplierAddedAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        Guid multiplierId, string name, decimal factor, MultiplierScope scope,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Config.MultiplierAdd,
            new MultiplierPayload(multiplierId, name, factor, scope),
            origin: origin, targetUserId: targetUserId, ct: ct);

    public static Task RecordMultiplierRemovedAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        Guid multiplierId, string name, decimal factor, MultiplierScope scope,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Config.MultiplierRemove,
            new MultiplierPayload(multiplierId, name, factor, scope),
            origin: origin, targetUserId: targetUserId, ct: ct);

    // ---- Currency ----------------------------------------------------------

    public static Task RecordBulkQueueAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        string code, long delta, int targetCount, string reason, Guid? batchId,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Currency.BulkQueue,
            new BulkQueuePayload(code, delta, targetCount, reason, batchId),
            origin: origin, targetUserId: targetUserId, ct: ct);

    public static Task RecordCurrencyCreatedAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        string code, string name, bool isSeasonal, bool isSpendable, CurrencyMode mode,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Currency.Create,
            new CurrencyDefinitionPayload(code, name, OldName: null, isSeasonal, isSpendable, mode),
            origin: origin, targetUserId: targetUserId, ct: ct);

    public static Task RecordCurrencyUpdatedAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        string code, string name, string? oldName, bool isSeasonal, bool isSpendable, CurrencyMode mode,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Currency.Update,
            new CurrencyDefinitionPayload(code, name, oldName, isSeasonal, isSpendable, mode),
            origin: origin, targetUserId: targetUserId, ct: ct);

    public static Task RecordWebhookCreatedAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        Guid webhookId, string url,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Currency.WebhookCreate,
            new WebhookPayload(webhookId, url, Enabled: null),
            origin: origin, targetUserId: targetUserId, ct: ct);

    public static Task RecordWebhookToggledAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        Guid webhookId, string url, bool enabled,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Currency.WebhookToggle,
            new WebhookPayload(webhookId, url, enabled),
            origin: origin, targetUserId: targetUserId, ct: ct);

    public static Task RecordWebhookDeletedAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        Guid webhookId, string url,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Currency.WebhookDelete,
            new WebhookPayload(webhookId, url, Enabled: null),
            origin: origin, targetUserId: targetUserId, ct: ct);

    // ---- Seasons -----------------------------------------------------------

    public static Task RecordSeasonStartedAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        Guid seasonId, string name,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Seasons.Start,
            new SeasonStartPayload(seasonId, name),
            origin: origin, targetUserId: targetUserId, ct: ct);

    public static Task RecordSeasonEndedAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        Guid seasonId, string name, DateTimeOffset endedAt,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Seasons.End,
            new SeasonEndPayload(seasonId, name, endedAt),
            origin: origin, targetUserId: targetUserId, ct: ct);

    // ---- Tracking ----------------------------------------------------------

    public static Task RecordSessionStartedAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        Guid sessionId, string name, ulong voiceChannelId, string? voiceChannelName,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Track.SessionStart,
            new SessionPayload(sessionId, name, voiceChannelId, voiceChannelName),
            origin: origin, targetUserId: targetUserId, ct: ct);

    public static Task RecordSessionStoppedAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        Guid sessionId, string name, ulong voiceChannelId, string? voiceChannelName,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Track.SessionStop,
            new SessionPayload(sessionId, name, voiceChannelId, voiceChannelName),
            origin: origin, targetUserId: targetUserId, ct: ct);

    // ---- API clients -------------------------------------------------------

    public static Task RecordApiClientCreatedAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        Guid clientId, string name, IReadOnlyList<string> scopes, ulong? actsAsUserId,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Api.ClientCreate,
            new ApiClientPayload(clientId, name, scopes, actsAsUserId),
            origin: origin, targetUserId: targetUserId, ct: ct);

    public static Task RecordApiClientRevokedAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        Guid clientId, string name,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Api.ClientRevoke,
            new ApiClientPayload(clientId, name, Array.Empty<string>(), ActsAsUserId: null),
            origin: origin, targetUserId: targetUserId, ct: ct);

    public static Task RecordCurrencySyncAllAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        string code,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Currency.SyncAll,
            new CurrencySyncAllPayload(code),
            origin: origin, targetUserId: targetUserId, ct: ct);

    public static Task RecordCurrencyConnectorAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        string code, CurrencyMode mode,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Currency.Connector,
            new CurrencyConnectorPayload(code, mode),
            origin: origin, targetUserId: targetUserId, ct: ct);

    public static Task RecordCurrencyRebuildAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        int corrected,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Currency.RebuildWallets,
            new CurrencyRebuildPayload(corrected),
            origin: origin, targetUserId: targetUserId, ct: ct);

    // ---- Tracking config ---------------------------------------------------

    public static Task RecordTrackingSettingsAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        TrackingSettingsPayload payload,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Config.TrackingSettings,
            payload,
            origin: origin, targetUserId: targetUserId, ct: ct);

    public static Task RecordTrackChannelAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        ulong channelId, string? channelName, GuildChannelKind kind, TrackedChannelMode mode,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Config.TrackChannel,
            new TrackedChannelPayload(channelId, channelName, kind, mode),
            origin: origin, targetUserId: targetUserId, ct: ct);

    public static Task RecordUntrackChannelAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        ulong channelId, string? channelName,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Config.UntrackChannel,
            new TrackedChannelRemovePayload(channelId, channelName),
            origin: origin, targetUserId: targetUserId, ct: ct);

    // ---- Quest config ------------------------------------------------------

    public static Task RecordQuestChannelAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        ulong? boardChannelId, ulong? modChannelId, int retentionHours,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Config.QuestChannel,
            new QuestChannelPayload(boardChannelId, modChannelId, retentionHours),
            origin: origin, targetUserId: targetUserId, ct: ct);

    public static Task RecordQuestAutomationAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        QuestAutomationPayload payload,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Config.QuestAutomation,
            payload,
            origin: origin, targetUserId: targetUserId, ct: ct);

    public static Task RecordQuestApprovalAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        bool requireIntakeApproval, FinalApprovalMode finalMode, bool allowSelfReview,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Config.QuestApproval,
            new QuestApprovalPayload(requireIntakeApproval, finalMode, allowSelfReview),
            origin: origin, targetUserId: targetUserId, ct: ct);

    public static Task RecordQuestTierPointsAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        long s, long a, long b, long c, long d, long e,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Config.QuestTierPoints,
            new QuestTierPointsPayload(s, a, b, c, d, e),
            origin: origin, targetUserId: targetUserId, ct: ct);

    // ---- Multipliers -------------------------------------------------------

    public static Task RecordMultiplierToggleAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        Guid multiplierId, bool enabled,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Config.MultiplierToggle,
            new MultiplierTogglePayload(multiplierId, enabled),
            origin: origin, targetUserId: targetUserId, ct: ct);

    // ---- Webhook delete (toggle/create already covered) --------------------

    public static Task RecordWebhookDeletedTypedAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        Guid webhookId, string url,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Currency.WebhookDelete,
            new WebhookDeletePayload(webhookId, url),
            origin: origin, targetUserId: targetUserId, ct: ct);

    // ---- Membership --------------------------------------------------------

    public static Task RecordMemberSyncAsync(
        this AuditService audit, ulong guildId, ulong actorId,
        string reason,
        ulong? targetUserId = null, AuditOrigin? origin = null, CancellationToken ct = default)
        => audit.RecordAsync(
            guildId, actorId,
            AuditActions.Members.SyncRequested,
            new MemberSyncPayload(reason),
            origin: origin, targetUserId: targetUserId, ct: ct);
}
