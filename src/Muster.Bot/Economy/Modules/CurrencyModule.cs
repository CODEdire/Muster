using Microsoft.Extensions.DependencyInjection;
using Muster.Contracts;
using Muster.Domain;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Commands.Currencies;
using Muster.Infrastructure.Services.Currencies;
using Muster.Persistence;
using Muster.Persistence.Queries;
using NetCord;
using NetCord.Services.ApplicationCommands;
using Wolverine;

namespace Muster.Bot.Economy.Modules;

/// <summary>
/// Discord adapter for the wallet/ledger, organized under <c>/currency</c>. Reads (balance) hit the read-side
/// query service; writes (give / mint / adjust) dispatch a CQRS command through the bus — the same authorized,
/// audited funnel as the web and public API. Members may <c>give</c> from their own wallet; economy staff
/// (officers + admins) <c>mint</c> and <c>adjust</c>. Authorization is enforced in the handler, not here.
/// </summary>
[SlashCommand("currency", "Wallets: check balances, send funds, and (staff) mint or adjust.")]
public class CurrencyModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SubSlashCommand("balance", "Show your wallet balances.")]
    public Task BalanceAsync()
        => RunAsync(async (sp, guildId) =>
        {
            var wallets = await sp.GetRequiredService<ICurrencyReadService>().GetWalletsAsync(guildId, Context.User.Id);
            if (wallets.Count == 0)
            {
                return CommandResult.Ok("You don't have any wallets yet.");
            }

            var lines = wallets.Select(w => $"**{w.Balance:N0}** {w.CurrencyCode} ({w.CurrencyName})");
            return CommandResult.Ok(string.Join("\n", lines));
        });

    [SubSlashCommand("history", "Show your recent currency transactions.")]
    public Task HistoryAsync(
        [SlashCommandParameter(Name = "currency", Description = "Filter to one currency (optional)", AutocompleteProviderType = typeof(CurrencyAutocompleteProvider))] string currency = "",
        [SlashCommandParameter(Name = "count", Description = "How many entries (default 10, max 25)")] int count = 10)
        => RunAsync(async (sp, guildId) =>
        {
            var code = string.IsNullOrWhiteSpace(currency) ? null : currency;
            var entries = await sp.GetRequiredService<ICurrencyReadService>()
                .GetMemberHistoryAsync(guildId, Context.User.Id, code, skip: 0, take: Math.Clamp(count, 1, 25));

            if (entries.Count == 0)
            {
                return CommandResult.Ok(code is null ? "No transactions yet." : $"No {code.ToUpperInvariant()} transactions yet.");
            }

            var lines = entries.Select(e =>
            {
                var sign = e.Amount >= 0 ? "+" : "";
                var note = string.IsNullOrWhiteSpace(e.Reason) ? "" : $" — {e.Reason}";
                return $"`{sign}{e.Amount:N0}` {e.CurrencyCode} · {e.SourceType} · <t:{e.OccurredAt.ToUnixTimeSeconds()}:R>{note}";
            });
            return CommandResult.Ok("**Recent activity**\n" + string.Join("\n", lines));
        });

    [SubSlashCommand("inspect", "Staff: view another member's wallet balances and recent activity.")]
    public Task InspectAsync(
        [SlashCommandParameter(Name = "member", Description = "Whose wallet to view")] User member,
        [SlashCommandParameter(Name = "count", Description = "History entries (default 10, max 25)")] int count = 10)
        => RunAsync(async (sp, guildId) =>
        {
            // Economy staff (officers + admins) may view anyone's wallet; a non-staff member only their own.
            var authorizer = sp.GetRequiredService<ICurrencyAuthorizer>();
            if (!await authorizer.AuthorizeAsync(new GuildActor(guildId, Context.User.Id), member.Id, CurrencyPermission.View))
            {
                return CommandResult.Error("You're not allowed to view other members' wallets.");
            }

            var reader = sp.GetRequiredService<ICurrencyReadService>();
            var wallets = await reader.GetWalletsAsync(guildId, member.Id);
            var balances = wallets.Count == 0
                ? "_no wallets_"
                : string.Join("\n", wallets.Select(w => $"**{w.Balance:N0}** {w.CurrencyCode} ({w.CurrencyName})"));

            var entries = await reader.GetMemberHistoryAsync(guildId, member.Id, null, skip: 0, take: Math.Clamp(count, 1, 25));
            var history = entries.Count == 0
                ? "_no activity_"
                : string.Join("\n", entries.Select(e =>
                {
                    var sign = e.Amount >= 0 ? "+" : "";
                    var note = string.IsNullOrWhiteSpace(e.Reason) ? "" : $" — {e.Reason}";
                    return $"`{sign}{e.Amount:N0}` {e.CurrencyCode} · {e.SourceType} · <t:{e.OccurredAt.ToUnixTimeSeconds()}:R>{note}";
                }));

            return CommandResult.Ok($"**Wallet — <@{member.Id}>**\n{balances}\n\n**Recent activity**\n{history}");
        });

    [SubSlashCommand("list", "See the server's currencies and what they're for.")]
    public Task ListAsync()
        => RunAsync(async (sp, guildId) =>
        {
            var currencies = await sp.GetRequiredService<ICurrencyReadService>().GetCurrenciesAsync(guildId);
            if (currencies.Count == 0)
            {
                return CommandResult.Ok("No currencies are configured in this server yet.");
            }

            var lines = currencies.Select(c =>
            {
                var tags = new List<string>();
                if (c.Seasonal) tags.Add("seasonal");
                tags.Add(c.Spendable ? "spendable" : "non-spendable");
                return $"**{c.Code}** — {c.Name} _({string.Join(", ", tags)})_";
            });
            return CommandResult.Ok("**Currencies**\n" + string.Join("\n", lines));
        });

    [SubSlashCommand("give", "Send some of your own currency to another member.")]
    public Task GiveAsync(
        [SlashCommandParameter(Name = "member", Description = "Who to send to")] User member,
        [SlashCommandParameter(Name = "currency", Description = "Currency", AutocompleteProviderType = typeof(CurrencyAutocompleteProvider))] string currency,
        [SlashCommandParameter(Name = "amount", Description = "How much to send")] long amount,
        [SlashCommandParameter(Name = "reason", Description = "Optional note")] string reason = "")
        => RunAsync(async (sp, guildId) =>
        {
            var result = await sp.GetRequiredService<IMessageBus>().InvokeAsync<Result>(
                new TransferCurrency(guildId, Context.User.Id, currency, Context.User.Id, member.Id, amount,
                    string.IsNullOrWhiteSpace(reason) ? $"Transfer from {Context.User.Username}" : reason.Trim()));
            return result.ToCurrencyResult($"Sent {amount:N0} {currency.ToUpperInvariant()} to <@{member.Id}>.");
        });

    [SubSlashCommand("mint", "Staff: create currency into a member's wallet.")]
    public Task MintAsync(
        [SlashCommandParameter(Name = "member", Description = "Who to credit")] User member,
        [SlashCommandParameter(Name = "currency", Description = "Currency", AutocompleteProviderType = typeof(CurrencyAutocompleteProvider))] string currency,
        [SlashCommandParameter(Name = "amount", Description = "Amount to mint (positive)")] long amount,
        [SlashCommandParameter(Name = "reason", Description = "Why")] string reason)
        => AdjustDispatch(member, currency, amount, reason, $"Minted {amount:N0} {currency.ToUpperInvariant()} to <@{member.Id}>.");

    [SubSlashCommand("adjust", "Staff: correct a member's balance by a signed amount (negative deducts).")]
    public Task AdjustAsync(
        [SlashCommandParameter(Name = "member", Description = "Whose balance to correct")] User member,
        [SlashCommandParameter(Name = "currency", Description = "Currency", AutocompleteProviderType = typeof(CurrencyAutocompleteProvider))] string currency,
        [SlashCommandParameter(Name = "delta", Description = "Signed amount (e.g. -50 to deduct)")] long delta,
        [SlashCommandParameter(Name = "reason", Description = "Why")] string reason)
        => AdjustDispatch(member, currency, delta, reason, $"Adjusted <@{member.Id}>'s {currency.ToUpperInvariant()} by {delta:N0}.");

    [SubSlashCommand("notify", "Turn DM receipts for currency you receive on or off.")]
    public Task NotifyAsync(
        [SlashCommandParameter(Name = "enabled", Description = "On to get a DM when you're sent or awarded currency")] bool enabled)
        => RunAsync(async (sp, guildId) =>
        {
            await sp.GetRequiredService<MusterDbContext>().SetCurrencyDmOptOutAsync(Context.User.Id, optOut: !enabled);
            return CommandResult.Ok(enabled
                ? "Currency receipts are **on** — you'll get a DM when you receive funds."
                : "Currency receipts are **off**.");
        });

    private Task AdjustDispatch(User member, string currency, long delta, string reason, string ok)
        => RunAsync(async (sp, guildId) =>
        {
            var result = await sp.GetRequiredService<IMessageBus>().InvokeAsync<Result>(
                new AdjustCurrency(guildId, Context.User.Id, currency, member.Id, delta, reason));
            return result.ToCurrencyResult(ok);
        });
}
