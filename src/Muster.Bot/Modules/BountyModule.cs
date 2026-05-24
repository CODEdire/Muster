using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Autocomplete;
using Muster.Infrastructure.Commands;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>
/// Discord adapter for the player bounty board. Posting/taking/etc. are open to participants (the
/// service enforces eligibility + ownership); arbitration requires a Quest Manager.
/// </summary>
public class BountyModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SlashCommand("bounty-post", "Post a bounty funded from your own balance (escrowed until completed).")]
    public Task<Reply> PostAsync(
        [SlashCommandParameter(Name = "name", Description = "What you need")] string name,
        [SlashCommandParameter(Name = "currency", Description = "Currency to offer", AutocompleteProviderType = typeof(CurrencyAutocompleteProvider))] string currency,
        [SlashCommandParameter(Name = "reward", Description = "Reward amount")] long reward,
        [SlashCommandParameter(Name = "details", Description = "Details (optional)")] string details = "",
        [SlashCommandParameter(Name = "expires-in-days", Description = "Auto-expire and refund after this many days (optional)")] long expiresInDays = 0)
        => RunAsync((sp, guildId) =>
        {
            var deadline = expiresInDays > 0 ? DateTimeOffset.UtcNow.AddDays(expiresInDays) : (DateTimeOffset?)null;
            return sp.GetRequiredService<BountyCommandService>().PostAsync(guildId, Context.User.Id, name, currency, reward, details, deadline);
        });

    [SlashCommand("bounty-list", "List open bounties.")]
    public Task<Reply> ListAsync()
        => RunAsync((sp, guildId) => sp.GetRequiredService<BountyCommandService>().ListAsync(guildId));

    [SlashCommand("bounty-take", "Take an open bounty to work on it.")]
    public Task<Reply> TakeAsync([SlashCommandParameter(Name = "bounty", Description = "Bounty", AutocompleteProviderType = typeof(BountyAutocompleteProvider))] string bounty)
        => RunAsync((sp, guildId) => sp.GetRequiredService<BountyCommandService>().TakeAsync(guildId, bounty, Context.User.Id));

    [SlashCommand("bounty-submit", "Submit a bounty you've completed for the owner to confirm.")]
    public Task<Reply> SubmitAsync([SlashCommandParameter(Name = "bounty", Description = "Bounty", AutocompleteProviderType = typeof(BountyAutocompleteProvider))] string bounty)
        => RunAsync((sp, guildId) => sp.GetRequiredService<BountyCommandService>().SubmitAsync(guildId, bounty, Context.User.Id));

    [SlashCommand("bounty-confirm", "Confirm your bounty is complete and pay the completer.")]
    public Task<Reply> ConfirmAsync([SlashCommandParameter(Name = "bounty", Description = "Bounty", AutocompleteProviderType = typeof(BountyAutocompleteProvider))] string bounty)
        => RunAsync((sp, guildId) => sp.GetRequiredService<BountyCommandService>().ConfirmAsync(guildId, bounty, Context.User.Id));

    [SlashCommand("bounty-cancel", "Cancel your open bounty and refund the escrow.")]
    public Task<Reply> CancelAsync([SlashCommandParameter(Name = "bounty", Description = "Bounty", AutocompleteProviderType = typeof(BountyAutocompleteProvider))] string bounty)
        => RunAsync((sp, guildId) => sp.GetRequiredService<BountyCommandService>().CancelAsync(guildId, bounty, Context.User.Id));

    [SlashCommand("bounty-dispute", "Raise a dispute on a submitted bounty for a Quest Manager to review.")]
    public Task<Reply> DisputeAsync([SlashCommandParameter(Name = "bounty", Description = "Bounty", AutocompleteProviderType = typeof(BountyAutocompleteProvider))] string bounty)
        => RunAsync((sp, guildId) => sp.GetRequiredService<BountyCommandService>().DisputeAsync(guildId, bounty, Context.User.Id));

    [SlashCommand("bounty-arbitrate", "Resolve a disputed bounty (Quest Manager).")]
    public Task<Reply> ArbitrateAsync(
        [SlashCommandParameter(Name = "bounty", Description = "Bounty", AutocompleteProviderType = typeof(BountyAutocompleteProvider))] string bounty,
        [SlashCommandParameter(Name = "pay", Description = "Pay the completer (true) or refund the owner (false)")] bool pay)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<BountyCommandService>().ArbitrateAsync(guildId, bounty, pay),
            RequiredRole.QuestManager, "bounty.arbitrate");
}
