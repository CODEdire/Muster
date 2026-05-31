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
        CreateMuster command, GuildAuthorizationService auth, MusterDbContext db, MusterService musters,
        GuildMusterSettingsService musterSettings, IMessageBus bus, CancellationToken ct)
    {
        // A Tracking Manager may create custom (and override a template); a template-only Muster Creator must pick a
        // template and can't override.
        var isManager = await auth.IsTrackingManagerAsync(command.GuildId, command.ActorId, ct);
        if (!isManager && !await auth.IsMusterCreatorAsync(command.GuildId, command.ActorId, ct))
        {
            return Result<Guid>.Fail("Forbidden");
        }

        Muster.Domain.Entities.Musters.MusterTemplate? template = null;
        if (command.TemplateId is { } tid)
        {
            template = await db.MusterTemplates.FirstOrDefaultAsync(t => t.Id == tid && t.GuildId == command.GuildId && t.Enabled, ct);
            if (template is null)
            {
                return Result<Guid>.Fail("TemplateNotFound");
            }
        }
        else if (!isManager)
        {
            return Result<Guid>.Fail("TemplateRequired"); // creators can't free-hand rewards
        }

        var defaults = await musterSettings.GetAsync(command.GuildId, ct);

        // Resolve effective values: template (if any) is the base; a manager's non-null custom fields override; with
        // no template, a manager's values fall back to the guild defaults.
        long points; long coins; Guid? coinCcy; int retention; int? capacity; DateTimeOffset? expires; int? minCheckIns;

        var now = DateTimeOffset.UtcNow;
        // Guild default max active time (0 = none). A template's own ExpiryHours wins; if a template doesn't set one,
        // it falls back to this guild default so even templated musters don't linger.
        DateTimeOffset? GuildExpiry() => defaults.DefaultExpiryHours > 0 ? now.AddHours(defaults.DefaultExpiryHours) : null;

        if (template is not null)
        {
            points = template.Points;
            coins = template.Coins;
            coinCcy = template.CoinCurrencyId;
            retention = template.RetentionHours;
            capacity = template.Capacity;
            minCheckIns = template.MinCheckIns;
            expires = template.ExpiryHours is { } eh ? now.AddHours(eh) : GuildExpiry();

            if (isManager) // overrides
            {
                if (command.Points is { } p) points = p;
                if (command.Coins is { } c) { coins = c; coinCcy = command.CoinCurrencyId ?? coinCcy; }
                if (command.Capacity is { } cap) capacity = cap;
                if (command.ExpiresAt is { } ex) expires = ex;
                if (command.MinCheckIns is { } mc) minCheckIns = mc;
            }
        }
        else
        {
            points = command.Points ?? defaults.DefaultPoints;
            coins = command.Coins ?? defaults.DefaultCoins;
            coinCcy = command.CoinCurrencyId ?? defaults.DefaultCoinCurrencyId;
            retention = defaults.BoardRetentionHours;
            capacity = command.Capacity;
            minCheckIns = command.MinCheckIns ?? defaults.DefaultMinCheckIns;
            expires = command.ExpiresAt ?? GuildExpiry();
        }

        if (points < 0 || coins < 0)
        {
            return Result<Guid>.Fail("RewardNegative");
        }

        if (capacity is <= 0)
        {
            return Result<Guid>.Fail("BadCapacity");
        }

        if (minCheckIns is < 0)
        {
            return Result<Guid>.Fail("BadMinimum");
        }

        // A minimum above a hard cap could never be reached — the muster would always pay nobody.
        if (capacity is { } capVal && minCheckIns is { } minVal && minVal > capVal)
        {
            return Result<Guid>.Fail("MinAboveCapacity");
        }

        // Coins require a spendable currency that belongs to this guild.
        if (coins > 0 && (coinCcy is not { } cc || !await db.Currencies.AnyAsync(c => c.Id == cc && c.GuildId == command.GuildId && c.IsSpendable, ct)))
        {
            return Result<Guid>.Fail("CoinCurrencyInvalid");
        }

        // Honor the guild's muster channel allow-list (empty = any). channelId 0 resolves to the configured default
        // at render time, which is itself validated when set — so only an explicit channel is checked here.
        if (!defaults.ChannelAllowed(command.ChannelId))
        {
            return Result<Guid>.Fail("ChannelNotAllowed");
        }

        // Card text: the author's value, else the template's default. Prompt is required (one of them must be set);
        // a template-locked creator can rely entirely on the template's prompt, so posting is one step.
        var prompt = (string.IsNullOrWhiteSpace(command.Prompt) ? template?.Prompt : command.Prompt)?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Result<Guid>.Fail("PromptRequired");
        }

        var title = (string.IsNullOrWhiteSpace(command.Title) ? template?.Title : command.Title)?.Trim();
        title = string.IsNullOrWhiteSpace(title) ? null : title;

        var checkInCreator = command.CheckInCreator ?? defaults.CreatorAutoCheckIn;

        var muster = await musters.CreateAsync(
            command.GuildId, command.ChannelId, title, prompt, points, coins, coinCcy, retention,
            capacity, expires, command.ActorId,
            sessionId: command.SessionId, checkInCreator: checkInCreator, minCheckIns: minCheckIns, ct: ct);

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
