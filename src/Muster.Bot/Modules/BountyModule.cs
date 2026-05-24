using Microsoft.Extensions.DependencyInjection;
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
    public Task<string> PostAsync(
        [SlashCommandParameter(Name = "name", Description = "What you need")] string name,
        [SlashCommandParameter(Name = "reward", Description = "Reward amount")] long reward,
        [SlashCommandParameter(Name = "currency", Description = "Currency code, e.g. COIN")] string currency,
        [SlashCommandParameter(Name = "details", Description = "Details (optional)")] string details = "")
        => RunAsync((sp, guildId) => sp.GetRequiredService<BountyCommandService>().PostAsync(guildId, Context.User.Id, name, currency, reward, details));

    [SlashCommand("bounty-list", "List open bounties.")]
    public Task<string> ListAsync()
        => RunAsync((sp, guildId) => sp.GetRequiredService<BountyCommandService>().ListAsync(guildId));

    [SlashCommand("bounty-take", "Take an open bounty to work on it.")]
    public Task<string> TakeAsync([SlashCommandParameter(Name = "bounty", Description = "Bounty id")] string bounty)
        => RunAsync((sp, guildId) => sp.GetRequiredService<BountyCommandService>().TakeAsync(guildId, bounty, Context.User.Id));

    [SlashCommand("bounty-submit", "Submit a bounty you've completed for the owner to confirm.")]
    public Task<string> SubmitAsync([SlashCommandParameter(Name = "bounty", Description = "Bounty id")] string bounty)
        => RunAsync((sp, guildId) => sp.GetRequiredService<BountyCommandService>().SubmitAsync(guildId, bounty, Context.User.Id));

    [SlashCommand("bounty-confirm", "Confirm your bounty is complete and pay the completer.")]
    public Task<string> ConfirmAsync([SlashCommandParameter(Name = "bounty", Description = "Bounty id")] string bounty)
        => RunAsync((sp, guildId) => sp.GetRequiredService<BountyCommandService>().ConfirmAsync(guildId, bounty, Context.User.Id));

    [SlashCommand("bounty-cancel", "Cancel your open bounty and refund the escrow.")]
    public Task<string> CancelAsync([SlashCommandParameter(Name = "bounty", Description = "Bounty id")] string bounty)
        => RunAsync((sp, guildId) => sp.GetRequiredService<BountyCommandService>().CancelAsync(guildId, bounty, Context.User.Id));

    [SlashCommand("bounty-dispute", "Raise a dispute on a submitted bounty for a Quest Manager to review.")]
    public Task<string> DisputeAsync([SlashCommandParameter(Name = "bounty", Description = "Bounty id")] string bounty)
        => RunAsync((sp, guildId) => sp.GetRequiredService<BountyCommandService>().DisputeAsync(guildId, bounty, Context.User.Id));

    [SlashCommand("bounty-arbitrate", "Resolve a disputed bounty (Quest Manager).")]
    public Task<string> ArbitrateAsync(
        [SlashCommandParameter(Name = "bounty", Description = "Bounty id")] string bounty,
        [SlashCommandParameter(Name = "pay", Description = "Pay the completer (true) or refund the owner (false)")] bool pay)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<BountyCommandService>().ArbitrateAsync(guildId, bounty, pay),
            RequiredRole.QuestManager, "bounty.arbitrate");
}
