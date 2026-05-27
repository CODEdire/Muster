using Microsoft.EntityFrameworkCore;
using Muster.Contracts;
using Muster.Infrastructure.Services.Currencies;
using Muster.Persistence;
using Muster.Persistence.Queries;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

namespace Muster.Bot;

/// <summary>
/// DMs a member a private receipt when they <i>receive</i> currency through a deliberate grant — a transfer in, or a
/// staff mint/adjustment that credits them. Earning side-effects (quest/muster/event/tracking awards) and house/escrow
/// legs are intentionally excluded: those are surfaced on their own board cards, and a firehose of every movement would
/// be noise. Members can opt out via <c>/currency notify off</c> or the web wallet (<see cref="Muster.Domain.Entities.Members.DiscordUser.CurrencyDmOptOut"/>).
///
/// Routed to the bot over the durable <see cref="WolverineExtensions.CurrencyEventsQueue"/>, so a grant issued from the
/// web or API still reaches the recipient. Best-effort: a closed DM is a no-op (the balance still moved).
/// </summary>
public static class CurrencyDmHandler
{
    // Deliberate grants worth a receipt. Earning sources (Quest/Muster/Event/TrackingSession), Connector echoes, and
    // Checkpoint folds are excluded. (Quest covers bounty escrow legs too — those have their own quest DMs.)
    private static readonly HashSet<string> GrantSources =
    [
        nameof(Muster.Domain.Enums.CurrencyLedgerSource.Transfer),
        nameof(Muster.Domain.Enums.CurrencyLedgerSource.Adjustment),
        nameof(Muster.Domain.Enums.CurrencyLedgerSource.ManualAward),
    ];

    /// <summary>Pure receipt-eligibility rule (no IO): a positive amount, a real member (not the house/escrow
    /// account), credited by a deliberate grant. The opt-out + DM delivery are layered on in <see cref="Handle"/>.</summary>
    public static bool ShouldNotify(string sourceType, long amount, ulong userId)
        => amount > 0 && userId != CurrencyService.EscrowAccountUserId && GrantSources.Contains(sourceType);

    public static async Task Handle(CurrencyMovementRecorded m, MusterDbContext db, GatewayClient client, CancellationToken ct)
    {
        if (!ShouldNotify(m.SourceType, m.Amount, m.UserId))
        {
            return;
        }

        var user = await db.FindUserAsync(m.UserId, ct);
        if (user is null || user.IsBot || user.CurrencyDmOptOut)
        {
            return;
        }

        var currency = await db.FindCurrencyByIdAsync(m.GuildId, m.CurrencyId, ct);
        if (currency is null)
        {
            return;
        }

        var guildName = await db.GuildNameAsync(m.GuildId, ct) ?? "your server";

        var embed = new EmbedProperties
        {
            Title = $"💰 You received {m.Amount:N0} {currency.Code}",
            Description = string.IsNullOrWhiteSpace(m.Reason) ? null : m.Reason,
            Color = new Color(0x2ECC71),
            Fields =
            [
                new() { Name = "Currency", Value = $"{currency.Code} — {currency.Name}", Inline = true },
                new() { Name = "Source", Value = SourceLabel(m.SourceType), Inline = true },
                new() { Name = "Server", Value = guildName, Inline = true },
            ],
            Footer = new EmbedFooterProperties { Text = "Turn these off with /currency notify off" },
        };

        await QuestDm.TrySendAsync(client, m.UserId, embed, []); // best-effort; DMs-closed → no-op
    }

    private static string SourceLabel(string sourceType) => sourceType switch
    {
        nameof(Muster.Domain.Enums.CurrencyLedgerSource.Transfer) => "Transfer received",
        nameof(Muster.Domain.Enums.CurrencyLedgerSource.Adjustment) => "Staff adjustment",
        nameof(Muster.Domain.Enums.CurrencyLedgerSource.ManualAward) => "Staff award",
        _ => sourceType,
    };
}
