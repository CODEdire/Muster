using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Autocomplete;
using Muster.Infrastructure.Commands;
using NetCord;
using NetCord.Services.ApplicationCommands;
using Muster.Infrastructure.Commands.Ledger;

namespace Muster.Bot.Modules;

/// <summary>Discord adapter for the award command (admin-only). Logic lives in <see cref="AwardCommandService"/>.</summary>
public class AwardModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SlashCommand("award", "Award a currency to a member for an off-platform contribution.")]
    public Task AwardAsync(
        [SlashCommandParameter(Name = "member", Description = "Member to award")] User member,
        [SlashCommandParameter(Name = "currency", Description = "Currency", AutocompleteProviderType = typeof(CurrencyAutocompleteProvider))] string currency,
        [SlashCommandParameter(Name = "amount", Description = "Amount to award")] long amount,
        [SlashCommandParameter(Name = "reason", Description = "Why they're being awarded")] string reason)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<AwardCommandService>()
                .AwardCurrencyAsync(guildId, Context.User.Id, member.Id, currency, amount, reason),
            RequiredRole.Admin,
            auditAction: "award");
}
