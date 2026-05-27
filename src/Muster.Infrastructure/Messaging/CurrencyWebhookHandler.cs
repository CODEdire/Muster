using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Muster.Contracts;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Messaging;

/// <summary>
/// Fans a staged <see cref="CurrencyMovementRecorded"/> out to the guild's enabled outbound webhooks whose source
/// filter matches, delivering a signed payload via <see cref="CurrencyWebhookDispatcher"/>. Best-effort: a failing
/// endpoint accrues failures (and auto-disables past the threshold) without failing the message, so one bad
/// subscriber can't block the others or trigger mass redelivery. Runs on the bot host (the currency-events listener).
/// </summary>
public static class CurrencyWebhookHandler
{
    public static async Task Handle(
        CurrencyMovementRecorded m, MusterDbContext db, CurrencyWebhookDispatcher dispatcher, ILogger<CurrencyMovementRecorded> logger, CancellationToken ct)
    {
        if (!Enum.TryParse<CurrencyLedgerSource>(m.SourceType, out var source))
        {
            return;
        }

        var hooks = (await db.EnabledWebhooksAsync(m.GuildId, ct)).Where(w => w.Matches(source)).ToList();
        if (hooks.Count == 0)
        {
            return;
        }

        var code = (await db.CurrencyCodeMapAsync(m.GuildId, ct)).GetValueOrDefault(m.CurrencyId, "?");
        var payload = new WebhookMovementPayload(
            m.GuildId, m.UserId, m.CurrencyId, code, m.Amount, m.SourceType, m.SourceId, m.Reason, m.SeasonId, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(payload);
        // Stable per-movement key so a subscriber can dedupe a redelivery (sourceId when present, else a composite).
        var deliveryId = m.SourceId ?? $"{m.GuildId}-{m.UserId}-{m.CurrencyId}-{m.Amount}";

        foreach (var hook in hooks)
        {
            var result = await dispatcher.SendAsync(hook, json, deliveryId, ct);
            CurrencyWebhookDispatcher.ApplyHealth(hook, result);
            if (!result.Ok)
            {
                logger.LogWarning("Webhook {Hook} delivery failed for guild {Guild}: {Error}", hook.Id, m.GuildId, result.Error);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
