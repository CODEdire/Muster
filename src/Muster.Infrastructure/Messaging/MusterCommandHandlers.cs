using Microsoft.EntityFrameworkCore;
using Muster.Contracts;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Musters;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Wolverine;

namespace Muster.Infrastructure.Messaging;

// =====================================================================================================
// Muster (reaction check-in) Wolverine handlers. The single enforcement point for every surface — slash
// command, Check-In button, web admin — so authorization + auditing live in one place. Lifecycle actions
// (create/close/link/edit roster) require TrackingManager; a member's own check-in does not (it's gated on
// participant-eligibility inside the service). Each handler is a plain static method; tests call them directly.
//
// Reward reversal is deliberately absent: a paid muster coin is undone via the EconomyManager currency-adjust
// path, not here (removing a participant only drops the roster row).
// =====================================================================================================

public static class CreateMusterHandler
{
    public static async Task<Result<Guid>> Handle(
        CreateMuster command, GuildAuthorizationService auth, MusterDbContext db, MusterService musters, IMessageBus bus, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.Prompt))
        {
            return Result<Guid>.Fail("PromptRequired");
        }

        if (command.Reward < 0)
        {
            return Result<Guid>.Fail("RewardNegative");
        }

        if (command.Capacity is <= 0)
        {
            return Result<Guid>.Fail("BadCapacity");
        }

        if (!await auth.IsTrackingManagerAsync(command.GuildId, command.ActorId, ct))
        {
            return Result<Guid>.Fail("Forbidden");
        }

        // A specified reward currency must belong to this guild (defense for any non-UI producer; the UI passes null).
        if (command.CurrencyId is { } cid && !await db.Currencies.AnyAsync(c => c.Id == cid && c.GuildId == command.GuildId, ct))
        {
            return Result<Guid>.Fail("CurrencyNotFound");
        }

        // Honor the guild's muster channel allow-list (empty = any channel). channelId 0 resolves to the configured
        // default at render time, which is itself validated when set — so only an explicit channel is checked here.
        var settings = await db.GetSettingsAsync(command.GuildId, ct);
        if (!settings.Musters.ChannelAllowed(command.ChannelId))
        {
            return Result<Guid>.Fail("ChannelNotAllowed");
        }

        var prompt = command.Prompt.Trim();
        var title = string.IsNullOrWhiteSpace(command.Title) ? null : command.Title.Trim();

        var muster = command.CurrencyId is { } currencyId
            ? await musters.CreateAsync(command.GuildId, command.ChannelId, title, prompt, currencyId, command.Reward,
                command.Capacity, command.ExpiresAt, command.ActorId, sessionId: command.SessionId, ct: ct)
            : await musters.CreatePointsAsync(command.GuildId, command.ChannelId, title, prompt, command.Reward,
                command.Capacity, command.ExpiresAt, command.ActorId, sessionId: command.SessionId, ct: ct);

        await bus.PublishAsync(new MusterChanged(command.GuildId, muster.Id, MusterChangeKind.Created));
        return Result<Guid>.Success(muster.Id);
    }
}

public static class CheckInMusterHandler
{
    public static async Task<Result> Handle(
        CheckInMuster command, MusterDbContext db, MusterService musters, IMessageBus bus, CancellationToken ct)
    {
        if (!await OwnsAsync(db, command.GuildId, command.MusterId, ct))
        {
            return Result.Fail("NotFound");
        }

        var outcome = await musters.CheckInAsync(command.MusterId, command.ActorId, MusterParticipantSource.Button, ct);
        if (outcome != ReactionOutcome.Recorded)
        {
            return Result.Fail(outcome.ToString());
        }

        await bus.PublishAsync(new MusterChanged(command.GuildId, command.MusterId, MusterChangeKind.CheckedIn));
        return Result.Success();
    }

    internal static Task<bool> OwnsAsync(MusterDbContext db, ulong guildId, Guid musterId, CancellationToken ct)
        => db.ReactionMusters.AnyAsync(m => m.Id == musterId && m.GuildId == guildId, ct);
}

public static class AddMusterParticipantHandler
{
    public static async Task<Result> Handle(
        AddMusterParticipant command, GuildAuthorizationService auth, MusterDbContext db, MusterService musters, IMessageBus bus, CancellationToken ct)
    {
        if (!await auth.IsTrackingManagerAsync(command.GuildId, command.ActorId, ct))
        {
            return Result.Fail("Forbidden");
        }

        if (!await CheckInMusterHandler.OwnsAsync(db, command.GuildId, command.MusterId, ct))
        {
            return Result.Fail("NotFound");
        }

        var outcome = await musters.CheckInAsync(command.MusterId, command.UserId, MusterParticipantSource.Admin, ct);
        if (outcome is not (ReactionOutcome.Recorded or ReactionOutcome.AlreadyParticipated))
        {
            return Result.Fail(outcome.ToString());
        }

        await bus.PublishAsync(new MusterChanged(command.GuildId, command.MusterId, MusterChangeKind.CheckedIn));
        return Result.Success();
    }
}

public static class RemoveMusterParticipantHandler
{
    public static async Task<Result> Handle(
        RemoveMusterParticipant command, GuildAuthorizationService auth, MusterDbContext db, MusterService musters, IMessageBus bus, CancellationToken ct)
    {
        if (!await auth.IsTrackingManagerAsync(command.GuildId, command.ActorId, ct))
        {
            return Result.Fail("Forbidden");
        }

        if (!await CheckInMusterHandler.OwnsAsync(db, command.GuildId, command.MusterId, ct))
        {
            return Result.Fail("NotFound");
        }

        if (!await musters.RemoveParticipantAsync(command.MusterId, command.UserId, ct))
        {
            return Result.Fail("NotAParticipant");
        }

        await bus.PublishAsync(new MusterChanged(command.GuildId, command.MusterId, MusterChangeKind.ParticipantsChanged));
        return Result.Success();
    }
}

public static class CloseMusterHandler
{
    public static async Task<Result> Handle(
        CloseMuster command, GuildAuthorizationService auth, MusterDbContext db, MusterService musters, IMessageBus bus, CancellationToken ct)
    {
        if (!await auth.IsTrackingManagerAsync(command.GuildId, command.ActorId, ct))
        {
            return Result.Fail("Forbidden");
        }

        if (!await CheckInMusterHandler.OwnsAsync(db, command.GuildId, command.MusterId, ct))
        {
            return Result.Fail("NotFound");
        }

        if (!await musters.CloseAsync(command.MusterId, MusterStatus.Closed, ct))
        {
            return Result.Fail("AlreadyClosed");
        }

        await bus.PublishAsync(new MusterChanged(command.GuildId, command.MusterId, MusterChangeKind.Closed));
        return Result.Success();
    }
}

public static class LinkMusterToSessionHandler
{
    public static async Task<Result> Handle(
        LinkMusterToSession command, GuildAuthorizationService auth, MusterDbContext db, MusterService musters, CancellationToken ct)
    {
        if (!await auth.IsTrackingManagerAsync(command.GuildId, command.ActorId, ct))
        {
            return Result.Fail("Forbidden");
        }

        if (!await CheckInMusterHandler.OwnsAsync(db, command.GuildId, command.MusterId, ct))
        {
            return Result.Fail("NotFound");
        }

        if (!await db.TrackingSessions.AnyAsync(s => s.Id == command.SessionId && s.GuildId == command.GuildId, ct))
        {
            return Result.Fail("SessionNotFound");
        }

        return await musters.LinkSessionAsync(command.MusterId, command.SessionId, ct)
            ? Result.Success()
            : Result.Fail("AlreadyLinked");
    }
}

public static class UnlinkMusterFromSessionHandler
{
    public static async Task<Result> Handle(
        UnlinkMusterFromSession command, GuildAuthorizationService auth, MusterDbContext db, MusterService musters, CancellationToken ct)
    {
        if (!await auth.IsTrackingManagerAsync(command.GuildId, command.ActorId, ct))
        {
            return Result.Fail("Forbidden");
        }

        if (!await CheckInMusterHandler.OwnsAsync(db, command.GuildId, command.MusterId, ct))
        {
            return Result.Fail("NotFound");
        }

        return await musters.UnlinkSessionAsync(command.MusterId, command.SessionId, ct)
            ? Result.Success()
            : Result.Fail("NotLinked");
    }
}

public static class SetSessionCoinGateHandler
{
    public static async Task<Result> Handle(
        SetSessionCoinGate command, GuildAuthorizationService auth, MusterDbContext db, CancellationToken ct)
    {
        if (!await auth.IsTrackingManagerAsync(command.GuildId, command.ActorId, ct))
        {
            return Result.Fail("Forbidden");
        }

        var session = await db.TrackingSessions
            .FirstOrDefaultAsync(s => s.Id == command.SessionId && s.GuildId == command.GuildId, ct);
        if (session is null)
        {
            return Result.Fail("SessionNotFound");
        }

        session.CoinGate = command.Gate;
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}
