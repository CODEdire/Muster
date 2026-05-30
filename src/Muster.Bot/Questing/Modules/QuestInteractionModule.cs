using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Platform.Telemetry;
using Muster.Contracts;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Quests;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ComponentInteractions;
using Wolverine;

namespace Muster.Bot.Questing.Modules;

/// <summary>
/// Handles the quest-card button clicks. Every button runs the <b>same CQRS command</b> as the slash/web/API path,
/// with <c>ActorId = the clicker</c>, so authorization is identical and auditing comes free — a button never bypasses
/// the funnel. The clicker gets an ephemeral result; the public/mod card updates itself via the lifecycle event the
/// command publishes. custom_id segments (guildId, questId, memberId) are bound by NetCord (separator <c>:</c>).
/// </summary>
public class QuestInteractionModule(IServiceScopeFactory scopeFactory) : ComponentInteractionModule<ButtonInteractionContext>
{
    private Task Run(IGuildCommand command, string ok) =>
        QuestInteractionDispatch.RunAsync(Context.Interaction, scopeFactory, command, ok);

    private Task OpenModal(ModalProperties modal) =>
        Context.Interaction.SendResponseAsync(InteractionCallback.Modal(modal));

    // ---- Worker / owner (public card) --------------------------------------

    [ComponentInteraction(QuestComponentBuilder.Claim)]
    public Task Claim(ulong guildId, Guid questId) => Run(new ClaimQuest(guildId, questId, Context.User.Id), "Claimed — tap **📨 My actions** to submit when you're done.");

    /// <summary>Public-card "My actions": privately DM the clicker the actions that apply to them (Submit for a
    /// claimer, owner controls for an owner). Falls back to an ephemeral panel if their DMs are closed.</summary>
    [ComponentInteraction(QuestComponentBuilder.Mine)]
    public async Task MyActions(ulong guildId, Guid questId)
    {
        using var scope = scopeFactory.CreateScope();
        var quest = await scope.ServiceProvider.GetRequiredService<IQuestReadService>().GetQuestDetailAsync(guildId, questId);
        if (quest is null)
        {
            await Ephemeral("That quest no longer exists.");
            return;
        }

        // The button DMs the embed to the clicker; scrub so a clicker who isn't owner/manager/the row's worker
        // doesn't get another participant's notes leaked into their DMs.
        var isManager = await scope.ServiceProvider.GetRequiredService<Muster.Infrastructure.Services.Membership.GuildAuthorizationService>()
            .IsQuestManagerAsync(guildId, Context.User.Id);
        quest = QuestDetailViewScrub.ForViewer(quest, Context.User.Id, isManager);

        var actions = QuestComponentBuilder.DmActions(quest, guildId, Context.User.Id);
        if (actions.Count == 0)
        {
            await Ephemeral("You have no actions on this quest right now.");
            return;
        }

        var embed = QuestEmbedRenderer.Render(quest, guildId, scope.ServiceProvider.GetRequiredService<IConfiguration>()["Web:BaseUrl"]);
        var sent = await QuestDm.TrySendAsync(Context.Client, Context.User.Id, embed, actions);

        await (sent
            ? Ephemeral("📬 Sent your actions to your DMs.")
            : Context.Interaction.SendResponseAsync(InteractionCallback.Message(new InteractionMessageProperties
            {
                Content = "I couldn't DM you (DMs closed?). Here are your actions:",
                Embeds = [embed],
                Components = actions,
                Flags = MessageFlags.Ephemeral,
            })));
    }

    private Task Ephemeral(string content) =>
        Context.Interaction.SendResponseAsync(InteractionCallback.Message(new InteractionMessageProperties { Content = content, Flags = MessageFlags.Ephemeral }));

    // Submit opens a modal so the worker can attach an optional note for the reviewer (handled below).
    [ComponentInteraction(QuestComponentBuilder.Submit)]
    public Task Submit(ulong guildId, Guid questId) =>
        Context.Interaction.SendResponseAsync(InteractionCallback.Modal(QuestComponentBuilder.BuildSubmitModal(guildId, questId)));

    [ComponentInteraction(QuestComponentBuilder.Confirm)]
    public Task Confirm(ulong guildId, Guid questId) => Run(new ConfirmQuest(guildId, questId, Context.User.Id), "Confirmed — the completer is paid (or sent for final sign-off).");

    [ComponentInteraction(QuestComponentBuilder.Dispute)]
    public Task Dispute(ulong guildId, Guid questId) =>
        OpenModal(QuestComponentBuilder.NoteModal(QuestComponentBuilder.DisputeModal, guildId, questId, null, "Dispute quest", "Why are you disputing? (optional)", "What went wrong…"));

    [ComponentInteraction(QuestComponentBuilder.Cancel)]
    public Task Cancel(ulong guildId, Guid questId) => Run(new CancelQuest(guildId, questId, Context.User.Id), "Quest cancelled (escrow refunded for a player quest).");

    [ComponentInteraction(QuestComponentBuilder.Abandon)]
    public Task Abandon(ulong guildId, Guid questId) => Run(new ReleaseQuestClaim(guildId, questId, Context.User.Id, Context.User.Id), "You abandoned this quest — the slot is open again.");

    [ComponentInteraction(QuestComponentBuilder.Revise)]
    public Task Revise(ulong guildId, Guid questId) =>
        OpenModal(QuestComponentBuilder.NoteModal(QuestComponentBuilder.ReviseOwnerModal, guildId, questId, null, "Request revision", "What to fix (optional)", "Tell the worker what to change…"));

    // ---- Manager review (mod card) -----------------------------------------

    [ComponentInteraction(QuestComponentBuilder.Approve)]
    public Task Approve(ulong guildId, Guid questId, ulong memberId) => Run(new ApproveQuestSubmission(guildId, questId, memberId, Context.User.Id, null), $"Approved <@{memberId}>.");

    [ComponentInteraction(QuestComponentBuilder.Reject)]
    public Task Reject(ulong guildId, Guid questId, ulong memberId) =>
        OpenModal(QuestComponentBuilder.NoteModal(QuestComponentBuilder.RejectModal, guildId, questId, memberId, "Reject submission", "Reason (optional)", "Why it's rejected…"));

    [ComponentInteraction(QuestComponentBuilder.ReviseMember)]
    public Task ReviseMember(ulong guildId, Guid questId, ulong memberId) =>
        OpenModal(QuestComponentBuilder.NoteModal(QuestComponentBuilder.ReviseMemberModal, guildId, questId, memberId, "Request revision", "What to fix (optional)", "Tell the worker what to change…"));

    [ComponentInteraction(QuestComponentBuilder.IntakeReject)]
    public Task IntakeReject(ulong guildId, Guid questId) => Run(new RejectQuestIntake(guildId, questId, Context.User.Id), "Rejected at intake — the owner's escrow was refunded.");

    // ---- Resolution (mod card) ---------------------------------------------

    [ComponentInteraction(QuestComponentBuilder.ArbPay)]
    public Task ArbPay(ulong guildId, Guid questId) => Run(new ArbitrateQuest(guildId, questId, Context.User.Id, true), "Resolved: paid the completer.");

    [ComponentInteraction(QuestComponentBuilder.ArbRefund)]
    public Task ArbRefund(ulong guildId, Guid questId) => Run(new ArbitrateQuest(guildId, questId, Context.User.Id, false), "Resolved: refunded the owner.");

    [ComponentInteraction(QuestComponentBuilder.FinPay)]
    public Task FinPay(ulong guildId, Guid questId) => Run(new FinalizeQuest(guildId, questId, Context.User.Id, true), "Finalized — paid the completer.");

    [ComponentInteraction(QuestComponentBuilder.FinRefund)]
    public Task FinRefund(ulong guildId, Guid questId) => Run(new FinalizeQuest(guildId, questId, Context.User.Id, false), "Finalized — refunded the owner.");
}

/// <summary>Select-menu handlers: pick a submitter to review, or accept a pending quest at a chosen tier.</summary>
public class QuestMenuInteractionModule(IServiceScopeFactory scopeFactory) : ComponentInteractionModule<StringMenuInteractionContext>
{
    /// <summary>Multi-submitter review: picking a submitter shows the Approve/Reject/Revise buttons for just them.</summary>
    [ComponentInteraction(QuestComponentBuilder.ReviewPick)]
    public async Task Pick(ulong guildId, Guid questId)
    {
        if (Context.SelectedValues.Count == 0 || !ulong.TryParse(Context.SelectedValues[0], out var memberId))
        {
            return;
        }

        await Context.Interaction.SendResponseAsync(InteractionCallback.Message(new InteractionMessageProperties
        {
            Content = $"Review <@{memberId}>'s submission:",
            Components = [QuestComponentBuilder.ReviewButtons(guildId, questId, memberId)],
            Flags = MessageFlags.Ephemeral,
        }));
    }

    /// <summary>Accept a pending player quest at the chosen difficulty tier.</summary>
    [ComponentInteraction(QuestComponentBuilder.IntakeTier)]
    public Task AcceptAtTier(ulong guildId, Guid questId)
    {
        _ = Enum.TryParse<QuestTier>(Context.SelectedValues.FirstOrDefault(), out var tier);
        return QuestInteractionDispatch.RunAsync(Context.Interaction, scopeFactory,
            new AcceptQuestIntake(guildId, questId, Context.User.Id, tier, false),
            tier == QuestTier.None ? "Accepted and opened." : $"Accepted at tier {tier} and opened.");
    }
}

/// <summary>Modal submissions — currently the Submit note. Reads the text input off the submitted modal and runs
/// the command as the submitter (same funnel + ephemeral result as buttons).</summary>
public class QuestModalInteractionModule(IServiceScopeFactory scopeFactory) : ComponentInteractionModule<ModalInteractionContext>
{
    private Task Run(IGuildCommand command, string ok) =>
        QuestInteractionDispatch.RunAsync(Context.Interaction, scopeFactory, command, ok);

    /// <summary>The optional note the user typed in the modal (null if blank).</summary>
    private string? Note()
    {
        var v = Context.Components
            .OfType<Label>().Select(l => l.Component)
            .OfType<TextInput>().FirstOrDefault(t => t.CustomId == QuestComponentBuilder.NoteInput)?.Value;
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    [ComponentInteraction(QuestComponentBuilder.SubmitModal)]
    public Task Submit(ulong guildId, Guid questId) =>
        Run(new SubmitQuest(guildId, questId, Context.User.Id, Note()), "Submitted for review.");

    [ComponentInteraction(QuestComponentBuilder.DisputeModal)]
    public Task Dispute(ulong guildId, Guid questId) =>
        Run(new DisputeQuest(guildId, questId, Context.User.Id, Note()), "Dispute raised — a Quest Manager will review it.");

    [ComponentInteraction(QuestComponentBuilder.ReviseOwnerModal)]
    public Task ReviseOwner(ulong guildId, Guid questId) =>
        Run(new RequestQuestRevision(guildId, questId, Context.User.Id, null, Note()), "Sent back to revise.");

    [ComponentInteraction(QuestComponentBuilder.RejectModal)]
    public Task Reject(ulong guildId, Guid questId, ulong memberId) =>
        Run(new RejectQuestSubmission(guildId, questId, memberId, Context.User.Id, Note()), $"Rejected <@{memberId}>'s submission.");

    [ComponentInteraction(QuestComponentBuilder.ReviseMemberModal)]
    public Task ReviseMember(ulong guildId, Guid questId, ulong memberId) =>
        Run(new RequestQuestRevision(guildId, questId, Context.User.Id, memberId, Note()), $"Sent <@{memberId}>'s submission back to revise.");
}

/// <summary>Best-effort DM of a per-user quest action card. Fails closed (returns false) when the user's DMs are
/// shut, so the caller can fall back to an ephemeral panel.</summary>
internal static class QuestDm
{
    public static async Task<bool> TrySendAsync(NetCord.Gateway.GatewayClient client, ulong userId,
        EmbedProperties embed, IReadOnlyList<IMessageComponentProperties> components)
    {
        try
        {
            var dm = await client.Rest.GetDMChannelAsync(userId);
            await dm.SendMessageAsync(new MessageProperties { Embeds = [embed], Components = components });
            return true;
        }
        catch (RestException)
        {
            return false;
        }
    }
}

/// <summary>Shared button/menu → command dispatch: ack ephemerally, run the command as the clicker, report back.</summary>
internal static class QuestInteractionDispatch
{
    public static async Task RunAsync(Interaction interaction, IServiceScopeFactory scopeFactory, IGuildCommand command, string ok)
    {
        // OTEL Server-kind span — surface = button/menu/modal (set by caller naming convention),
        // actionKey = command type name (ClaimQuest, SubmitQuest, …). Renders on App Insights
        // Performance blade alongside slash spans.
        var actionKey = $"quest:{command.GetType().Name}";
        using var activity = BotTelemetry.StartInteraction(
            "interaction", actionKey, command.GuildId, interaction.User.Id, interaction.Channel?.Id);

        // Ack within 3s; the public card updates separately via the command's lifecycle event.
        await interaction.SendResponseAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

        Result result;
        try
        {
            using var scope = scopeFactory.CreateScope();
            result = await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<Result>(command);
            activity.SetResult(result.Ok, reason: result.Ok ? null : result.Status.ToString());
        }
        catch (Exception ex)
        {
            activity.SetException(ex);
            throw;
        }

        var content = result.Ok ? $"✅ {ok}" : $"⚠️ Couldn't do that — {result.Status}.";
        await interaction.SendFollowupMessageAsync(new InteractionMessageProperties { Content = content, Flags = MessageFlags.Ephemeral });
    }
}
