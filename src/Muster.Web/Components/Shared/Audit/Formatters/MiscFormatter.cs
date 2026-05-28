using System.Text.Json;
using Muster.Infrastructure.Services.Platform;

namespace Muster.Web.Components.Shared.Audit.Formatters;

/// <summary>
/// Catch-up formatter for the small-payload actions that don't justify their own class — seasons, currency
/// definitions, webhook lifecycle, API clients, member sync, sessions, bulk queue. Each branch is short.
/// </summary>
public class MiscFormatter : IAuditFormatter
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> _keys =
    [
        AuditActions.Seasons.Start.Key, AuditActions.Seasons.End.Key,
        AuditActions.Currency.Create.Key, AuditActions.Currency.Update.Key,
        AuditActions.Currency.SyncAll.Key, AuditActions.Currency.Connector.Key,
        AuditActions.Currency.RebuildWallets.Key, AuditActions.Currency.BulkQueue.Key,
        AuditActions.Currency.WebhookCreate.Key, AuditActions.Currency.WebhookToggle.Key,
        AuditActions.Currency.WebhookDelete.Key,
        AuditActions.Api.ClientCreate.Key, AuditActions.Api.ClientRevoke.Key,
        AuditActions.Members.SyncRequested.Key,
        AuditActions.Track.SessionStart.Key, AuditActions.Track.SessionStop.Key,
    ];

    public bool Matches(string action) => _keys.Contains(action);

    public AuditRender Render(AuditEntryView entry)
    {
        if (string.IsNullOrEmpty(entry.Details)) return Empty(entry);

        try
        {
            return entry.Action switch
            {
                var k when k == AuditActions.Seasons.Start.Key
                    => RenderSeasonStart(entry, JsonSerializer.Deserialize<SeasonStartPayload>(entry.Details, _json)),
                var k when k == AuditActions.Seasons.End.Key
                    => RenderSeasonEnd(entry, JsonSerializer.Deserialize<SeasonEndPayload>(entry.Details, _json)),
                var k when k == AuditActions.Currency.Create.Key
                    => RenderCurrencyDef(entry, "Created currency", JsonSerializer.Deserialize<CurrencyDefinitionPayload>(entry.Details, _json)),
                var k when k == AuditActions.Currency.Update.Key
                    => RenderCurrencyDef(entry, "Updated currency", JsonSerializer.Deserialize<CurrencyDefinitionPayload>(entry.Details, _json)),
                var k when k == AuditActions.Currency.SyncAll.Key
                    => RenderSyncAll(entry, JsonSerializer.Deserialize<CurrencySyncAllPayload>(entry.Details, _json)),
                var k when k == AuditActions.Currency.Connector.Key
                    => RenderConnector(entry, JsonSerializer.Deserialize<CurrencyConnectorPayload>(entry.Details, _json)),
                var k when k == AuditActions.Currency.RebuildWallets.Key
                    => RenderRebuild(entry, JsonSerializer.Deserialize<CurrencyRebuildPayload>(entry.Details, _json)),
                var k when k == AuditActions.Currency.BulkQueue.Key
                    => RenderBulk(entry, JsonSerializer.Deserialize<BulkQueuePayload>(entry.Details, _json)),
                var k when k == AuditActions.Currency.WebhookCreate.Key
                    => RenderWebhookCreated(entry, JsonSerializer.Deserialize<WebhookPayload>(entry.Details, _json)),
                var k when k == AuditActions.Currency.WebhookToggle.Key
                    => RenderWebhookToggled(entry, JsonSerializer.Deserialize<WebhookPayload>(entry.Details, _json)),
                var k when k == AuditActions.Currency.WebhookDelete.Key
                    => RenderWebhookDeleted(entry, JsonSerializer.Deserialize<WebhookDeletePayload>(entry.Details, _json)),
                var k when k == AuditActions.Api.ClientCreate.Key
                    => RenderApiCreate(entry, JsonSerializer.Deserialize<ApiClientPayload>(entry.Details, _json)),
                var k when k == AuditActions.Api.ClientRevoke.Key
                    => RenderApiRevoke(entry, JsonSerializer.Deserialize<ApiClientPayload>(entry.Details, _json)),
                var k when k == AuditActions.Members.SyncRequested.Key
                    => RenderMemberSync(entry, JsonSerializer.Deserialize<MemberSyncPayload>(entry.Details, _json)),
                var k when k == AuditActions.Track.SessionStart.Key
                    => RenderSession(entry, "Started session", JsonSerializer.Deserialize<SessionPayload>(entry.Details, _json)),
                var k when k == AuditActions.Track.SessionStop.Key
                    => RenderSession(entry, "Ended session", JsonSerializer.Deserialize<SessionPayload>(entry.Details, _json)),
                _ => Empty(entry),
            };
        }
        catch (JsonException)
        {
            return Empty(entry);
        }
    }

    private static AuditRender RenderSeasonStart(AuditEntryView entry, SeasonStartPayload? p)
    {
        if (p is null) return Empty(entry);
        return new AuditRender(
            Summary: [new TextToken("Started season "), new TextToken(p.Name, AuditStyle.Bold)],
            Sections: [Section("Season", ("Name", [new TextToken(p.Name)]), ("Id", [new CodeToken(p.SeasonId.ToString())]))]);
    }

    private static AuditRender RenderSeasonEnd(AuditEntryView entry, SeasonEndPayload? p)
    {
        if (p is null) return Empty(entry);
        return new AuditRender(
            Summary: [new TextToken("Ended season "), new TextToken(p.Name, AuditStyle.Bold)],
            Sections: [Section("Season",
                ("Name", [new TextToken(p.Name)]),
                ("Id", [new CodeToken(p.SeasonId.ToString())]),
                ("Ended", [new TextToken(p.EndedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'"))]))]);
    }

    private static AuditRender RenderCurrencyDef(AuditEntryView entry, string verb, CurrencyDefinitionPayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken($"{verb} "), new CurrencyToken(p.Code),
            new TextToken(" — "), new TextToken(p.Name, AuditStyle.Bold),
        };
        return new AuditRender(summary, [Section("Currency",
            ("Code", [new CurrencyToken(p.Code)]),
            ("Name", [new TextToken(p.Name)]),
            ("Old name", p.OldName is null ? [new TextToken("—", AuditStyle.Muted)] : [new TextToken(p.OldName)]),
            ("Seasonal", [new TextToken(p.IsSeasonal ? "yes" : "no")]),
            ("Spendable", [new TextToken(p.IsSpendable ? "yes" : "no")]),
            ("Mode", [new TextToken(p.Mode.ToString())]))]);
    }

    private static AuditRender RenderSyncAll(AuditEntryView entry, CurrencySyncAllPayload? p)
    {
        if (p is null) return Empty(entry);
        return new AuditRender(
            Summary: [new TextToken("Started balance sync for "), new CurrencyToken(p.CurrencyCode)],
            Sections: [Section("Sync all", ("Currency", [new CurrencyToken(p.CurrencyCode)]))]);
    }

    private static AuditRender RenderConnector(AuditEntryView entry, CurrencyConnectorPayload? p)
    {
        if (p is null) return Empty(entry);
        return new AuditRender(
            Summary: [new TextToken("Saved connector for "), new CurrencyToken(p.CurrencyCode), new TextToken($" ({p.Mode})", AuditStyle.Muted)],
            Sections: [Section("Connector",
                ("Currency", [new CurrencyToken(p.CurrencyCode)]),
                ("Mode", [new TextToken(p.Mode.ToString())]))]);
    }

    private static AuditRender RenderRebuild(AuditEntryView entry, CurrencyRebuildPayload? p)
    {
        if (p is null) return Empty(entry);
        return new AuditRender(
            Summary: [new TextToken("Rebuilt wallets — "), new TextToken($"{p.Corrected} corrected", AuditStyle.Bold)],
            Sections: [Section("Wallet rebuild", ("Balances corrected", [new TextToken(p.Corrected.ToString("N0"))]))]);
    }

    private static AuditRender RenderBulk(AuditEntryView entry, BulkQueuePayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken("Queued bulk "),
            new AmountToken(p.Delta, p.CurrencyCode),
            new TextToken($" × {p.TargetCount} members", AuditStyle.Muted),
        };
        return new AuditRender(summary, [Section("Bulk adjustment",
            ("Currency", [new CurrencyToken(p.CurrencyCode)]),
            ("Delta", [new AmountToken(p.Delta, p.CurrencyCode)]),
            ("Targets", [new TextToken(p.TargetCount.ToString("N0"))]),
            ("Reason", [new TextToken(p.Reason)]),
            ("Batch id", p.BatchId is { } b ? [new CodeToken(b.ToString())] : [new TextToken("—", AuditStyle.Muted)]))]);
    }

    private static AuditRender RenderWebhookCreated(AuditEntryView entry, WebhookPayload? p)
    {
        if (p is null) return Empty(entry);
        return new AuditRender(
            Summary: [new TextToken("Created webhook "), new CodeToken(p.Url)],
            Sections: [Section("Webhook", ("Id", [new CodeToken(p.WebhookId.ToString())]), ("URL", [new CodeToken(p.Url)]))]);
    }

    private static AuditRender RenderWebhookToggled(AuditEntryView entry, WebhookPayload? p)
    {
        if (p is null) return Empty(entry);
        var enabled = p.Enabled ?? false;
        return new AuditRender(
            Summary: [new TextToken(enabled ? "Enabled webhook " : "Disabled webhook "), new CodeToken(p.Url)],
            Sections: [Section("Webhook",
                ("Id", [new CodeToken(p.WebhookId.ToString())]),
                ("URL", [new CodeToken(p.Url)]),
                ("State", [new TextToken(enabled ? "enabled" : "disabled", enabled ? AuditStyle.Positive : AuditStyle.Muted)]))]);
    }

    private static AuditRender RenderWebhookDeleted(AuditEntryView entry, WebhookDeletePayload? p)
    {
        if (p is null) return Empty(entry);
        return new AuditRender(
            Summary: [new TextToken("Deleted webhook "), new CodeToken(p.Url)],
            Sections: [Section("Webhook", ("Id", [new CodeToken(p.WebhookId.ToString())]), ("URL", [new CodeToken(p.Url)]))]);
    }

    private static AuditRender RenderApiCreate(AuditEntryView entry, ApiClientPayload? p)
    {
        if (p is null) return Empty(entry);
        return new AuditRender(
            Summary: [new TextToken("Issued API client "), new TextToken(p.Name, AuditStyle.Bold)],
            Sections: [Section("API client",
                ("Name", [new TextToken(p.Name)]),
                ("Scopes", [new TextToken(p.Scopes.Count == 0 ? "—" : string.Join(", ", p.Scopes), p.Scopes.Count == 0 ? AuditStyle.Muted : AuditStyle.None)]),
                ("Acts as", p.ActsAsUserId is { } u ? [new UserToken(u)] : [new TextToken("—", AuditStyle.Muted)]),
                ("Id", [new CodeToken(p.ClientId.ToString())]))]);
    }

    private static AuditRender RenderApiRevoke(AuditEntryView entry, ApiClientPayload? p)
    {
        if (p is null) return Empty(entry);
        return new AuditRender(
            Summary: [new TextToken("Revoked API client "), new TextToken(p.Name, AuditStyle.Bold)],
            Sections: [Section("API client",
                ("Name", [new TextToken(p.Name)]),
                ("Id", [new CodeToken(p.ClientId.ToString())]))]);
    }

    private static AuditRender RenderMemberSync(AuditEntryView entry, MemberSyncPayload? p)
    {
        if (p is null) return Empty(entry);
        return new AuditRender(
            Summary: [new TextToken("Requested member sync — "), new TextToken(p.Reason, AuditStyle.Muted)],
            Sections: [Section("Member sync", ("Reason", [new TextToken(p.Reason)]))]);
    }

    private static AuditRender RenderSession(AuditEntryView entry, string verb, SessionPayload? p)
    {
        if (p is null) return Empty(entry);
        var summary = new List<AuditToken>
        {
            new TextToken($"{verb} "),
            new TextToken(p.Name, AuditStyle.Bold),
        };
        if (p.VoiceChannelId != 0)
        {
            summary.Add(new TextToken(" in "));
            summary.Add(new ChannelToken(p.VoiceChannelId));
        }
        return new AuditRender(summary, [Section("Session",
            ("Name", [new TextToken(p.Name)]),
            ("Voice channel", p.VoiceChannelId == 0 ? [new TextToken("—", AuditStyle.Muted)] : [new ChannelToken(p.VoiceChannelId)]),
            ("Channel name", p.VoiceChannelName is null ? [new TextToken("—", AuditStyle.Muted)] : [new TextToken(p.VoiceChannelName)]),
            ("Session id", p.SessionId == Guid.Empty ? [new TextToken("—", AuditStyle.Muted)] : [new CodeToken(p.SessionId.ToString())]))]);
    }

    private static AuditSection Section(string heading, params (string Label, IReadOnlyList<AuditToken> Value)[] rows)
        => new(heading, Body: [], Rows: rows.Select(r => new AuditSectionRow(r.Label, r.Value)).ToList());

    private static AuditRender Empty(AuditEntryView entry) =>
        new(
            Summary: [new TextToken(entry.Details ?? "", AuditStyle.Muted)],
            Sections: [new AuditSection("Details", [new TextToken(entry.Details ?? "")])]);
}
