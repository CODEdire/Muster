using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Musters.Rendering;
using Muster.Contracts;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ComponentInteractions;
using Wolverine;

namespace Muster.Bot.Musters.Modules;

/// <summary>
/// Handles the muster card's Check-In button. The click runs the <b>same CQRS command</b> as any other surface, with
/// <c>ActorId = the clicker</c>, so authorization + eligibility + auditing come free and a button never bypasses the
/// funnel. The clicker gets an ephemeral result; the public card updates itself via the <c>MusterChanged</c> event the
/// command publishes. custom_id segments (guildId, musterId) are bound by NetCord (separator <c>:</c>).
/// </summary>
public class MusterInteractionModule(IServiceScopeFactory scopeFactory) : ComponentInteractionModule<ButtonInteractionContext>
{
    [ComponentInteraction(MusterComponentBuilder.CheckIn)]
    public async Task CheckIn(ulong guildId, Guid musterId)
    {
        // Ack within 3s; the public card updates separately via the command's MusterChanged event.
        await Context.Interaction.SendResponseAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

        using var scope = scopeFactory.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<Result>(new CheckInMuster(guildId, Context.User.Id, musterId));

        await Context.Interaction.SendFollowupMessageAsync(
            new InteractionMessageProperties { Content = MusterResultText.CheckIn(result), Flags = MessageFlags.Ephemeral });
    }
}
