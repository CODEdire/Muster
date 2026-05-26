using Muster.Contracts;
using Muster.Domain.Entities;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Quests;
using Wolverine;
using Wolverine.Http;

namespace Muster.Web.Api;

// Request bodies. None carry an *actor* id — every action runs as the key's bound user (ApiClient.ActsAsUserId),
// so a key can never act on behalf of someone else. memberId on review bodies is the *subject* (whose
// submission), not the actor.
public record ApiPostQuest(string Origin, string Name, string Currency, long Reward, string? Description,
    string? Tier, bool RequestFinalApproval = false, int Capacity = 1);
public record ApiSubmit(string? Note);
public record ApiReview(ulong MemberId, string? Note);
public record ApiRevision(ulong? MemberId, string? Note);
public record ApiReopen(ulong MemberId);
public record ApiRelease(ulong MemberId);
public record ApiAcceptIntake(string Tier, bool RequireFinalApproval = false);
public record ApiPayChoice(bool Pay);
public record ApiEditQuest(string? Name, string? Description, long? Reward, DateTimeOffset? Deadline, string? Tier, int? Capacity);

/// <summary>
/// Public quest API under <c>/api/v1/guilds/{guildId}/quests</c>. Auth is declarative: <see cref="RequireApiScopeAttribute"/>
/// runs <see cref="ApiKeyMiddleware"/> ahead of each handler to validate the key + scope and inject the resolved
/// <see cref="ApiClient"/>. Two independent layers still gate every call — the <b>token</b> holds the scope
/// (<c>read:quests</c> / <c>write:quests</c>), and the key's <b>bound actor</b> (<see cref="ApiClient.ActsAsUserId"/>)
/// must have the guild permission (the command funnel authorizes that actor exactly as for the bot/web). Writes
/// require a bound actor (<c>requireActor: true</c>) and invoke the same CQRS commands via <see cref="IMessageBus"/>.
/// </summary>
public static class ApiQuestEndpoints
{
    private const string Read = "read:quests";
    private const string Write = "write:quests";

    // ---- Reads -------------------------------------------------------------

    // Full board parity with the web UI: tab (active|mine|history), type (guild|personal), search, sort, paging.
    // "mine"/"history" and the manager view key off the key's bound actor; an unbound key sees the public active
    // board as a non-manager. Manager-only rows (e.g. submissions to review, pending-intake) appear only when the
    // actor is a quest manager — same rule as the web board.
    [WolverineGet("/api/v1/guilds/{guildId}/quests")]
    [RequireApiScope(Read)]
    public static async Task<IResult> List(ulong guildId, HttpContext http, IQuestReadService reads, GuildAuthorizationService auth,
        string? tab = null, string? type = null, string? search = null, string? sort = null,
        bool desc = true, int page = 1, int size = 25)
    {
        var actor = http.ApiActor();
        var isManager = actor != 0 && await auth.IsQuestManagerAsync(guildId, actor);

        var currentTab = tab is "actionneeded" or "history" ? tab : "active";
        var currentType = type is "guild" or "player" or "mine" ? type : null;
        var currentSort = string.IsNullOrEmpty(sort) ? "created" : sort;
        var currentDesc = string.IsNullOrEmpty(sort) ? true : desc;
        var pageSize = size is 10 or 25 or 50 or 100 ? size : 25;

        var board = await reads.GetBoardAsync(guildId, actor, isManager, currentTab, currentType, search, currentSort, currentDesc, page, pageSize);
        return Results.Ok(new
        {
            items = board.Items,
            total = board.Total,
            page = board.Page,
            totalPages = board.TotalPages,
            codes = board.Codes,
            names = board.Names,
            avatars = board.Avatars,
        });
    }

    [WolverineGet("/api/v1/guilds/{guildId}/quests/{questId}")]
    [RequireApiScope(Read)]
    public static async Task<IResult> Detail(ulong guildId, Guid questId, IQuestReadService reads)
    {
        var quest = await reads.GetQuestDetailAsync(guildId, questId);
        return quest is null ? Results.NotFound(new { error = "quest_not_found" }) : Results.Ok(quest);
    }

    // ---- Writes (run as the bound actor) -----------------------------------

    [WolverinePost("/api/v1/guilds/{guildId}/quests")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> Post(ulong guildId, ApiPostQuest body, HttpContext http, IMessageBus bus)
    {
        if (!Enum.TryParse<QuestOrigin>(body.Origin, ignoreCase: true, out var origin))
        {
            return ToResult(Result.Fail("Origin must be 'Guild' or 'Player'."));
        }

        _ = Enum.TryParse<QuestTier>(body.Tier, ignoreCase: true, out var tier);
        return ToResult(await bus.InvokeAsync<Result>(new PostQuest(
            guildId, http.ApiActor(), origin, body.Name, body.Currency, body.Reward, body.Description ?? "",
            null, null, tier, body.RequestFinalApproval, body.Capacity < 1 ? 1 : body.Capacity)));
    }

    [WolverinePost("/api/v1/guilds/{guildId}/quests/{questId}/claim")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> Claim(ulong guildId, Guid questId, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new ClaimQuest(guildId, questId, http.ApiActor())));

    [WolverinePost("/api/v1/guilds/{guildId}/quests/{questId}/submit")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> Submit(ulong guildId, Guid questId, ApiSubmit body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new SubmitQuest(guildId, questId, http.ApiActor(), body.Note)));

    // Abandon the actor's own claim (self-release). The slot reopens; the quest stays Open.
    [WolverinePost("/api/v1/guilds/{guildId}/quests/{questId}/abandon")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> Abandon(ulong guildId, Guid questId, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new ReleaseQuestClaim(guildId, questId, http.ApiActor(), http.ApiActor())));

    // Manager force-release of another member's claim (memberId = whose claim to free).
    [WolverinePost("/api/v1/guilds/{guildId}/quests/{questId}/release")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> Release(ulong guildId, Guid questId, ApiRelease body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new ReleaseQuestClaim(guildId, questId, http.ApiActor(), body.MemberId)));

    [WolverinePost("/api/v1/guilds/{guildId}/quests/{questId}/approve")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> Approve(ulong guildId, Guid questId, ApiReview body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new ApproveQuestSubmission(guildId, questId, body.MemberId, http.ApiActor(), body.Note)));

    [WolverinePost("/api/v1/guilds/{guildId}/quests/{questId}/reject")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> Reject(ulong guildId, Guid questId, ApiReview body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new RejectQuestSubmission(guildId, questId, body.MemberId, http.ApiActor(), body.Note)));

    [WolverinePost("/api/v1/guilds/{guildId}/quests/{questId}/request-revision")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> RequestRevision(ulong guildId, Guid questId, ApiRevision body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new RequestQuestRevision(guildId, questId, http.ApiActor(), body.MemberId, body.Note)));

    [WolverinePost("/api/v1/guilds/{guildId}/quests/{questId}/reopen")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> Reopen(ulong guildId, Guid questId, ApiReopen body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new ReopenQuestRejection(guildId, questId, body.MemberId, http.ApiActor())));

    [WolverinePost("/api/v1/guilds/{guildId}/quests/{questId}/confirm")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> Confirm(ulong guildId, Guid questId, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new ConfirmQuest(guildId, questId, http.ApiActor())));

    [WolverinePost("/api/v1/guilds/{guildId}/quests/{questId}/dispute")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> Dispute(ulong guildId, Guid questId, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new DisputeQuest(guildId, questId, http.ApiActor())));

    [WolverinePost("/api/v1/guilds/{guildId}/quests/{questId}/edit")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> Edit(ulong guildId, Guid questId, ApiEditQuest body, HttpContext http, IMessageBus bus)
    {
        QuestTier? tier = Enum.TryParse<QuestTier>(body.Tier, ignoreCase: true, out var t) ? t : null;
        return ToResult(await bus.InvokeAsync<Result>(new EditQuest(guildId, questId, http.ApiActor(), body.Name, body.Description, body.Reward, body.Deadline, tier, body.Capacity)));
    }

    [WolverinePost("/api/v1/guilds/{guildId}/quests/{questId}/cancel")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> Cancel(ulong guildId, Guid questId, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new CancelQuest(guildId, questId, http.ApiActor())));

    [WolverinePost("/api/v1/guilds/{guildId}/quests/{questId}/arbitrate")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> Arbitrate(ulong guildId, Guid questId, ApiPayChoice body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new ArbitrateQuest(guildId, questId, http.ApiActor(), body.Pay)));

    [WolverinePost("/api/v1/guilds/{guildId}/quests/{questId}/intake/accept")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> AcceptIntake(ulong guildId, Guid questId, ApiAcceptIntake body, HttpContext http, IMessageBus bus)
    {
        _ = Enum.TryParse<QuestTier>(body.Tier, ignoreCase: true, out var tier);
        return ToResult(await bus.InvokeAsync<Result>(new AcceptQuestIntake(guildId, questId, http.ApiActor(), tier, body.RequireFinalApproval)));
    }

    [WolverinePost("/api/v1/guilds/{guildId}/quests/{questId}/intake/reject")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> RejectIntake(ulong guildId, Guid questId, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new RejectQuestIntake(guildId, questId, http.ApiActor())));

    [WolverinePost("/api/v1/guilds/{guildId}/quests/{questId}/finalize")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> Finalize(ulong guildId, Guid questId, ApiPayChoice body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new FinalizeQuest(guildId, questId, http.ApiActor(), body.Pay)));

    // ---- Helpers -----------------------------------------------------------

    private static IResult ToResult(Result result) =>
        result.Ok
            ? Results.Ok(new { ok = true })
            : Results.Json(new { error = "quest_action_failed", reason = result.Status }, statusCode: StatusCodes.Status409Conflict);
}
