using Microsoft.AspNetCore.Components;
using Muster.Contracts;
using Muster.Domain.Entities.Guilds;
using Muster.Domain.Entities.Quests;
using Muster.Domain.Enums;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Commands.Quests;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Platform;
using Muster.Web.Components.Pages.Quests.Models;

namespace Muster.Web.Components.Pages.Quests;

public partial class QuestPost
{
    [SupplyParameterFromForm(FormName = "post-quest")] private PostInput Post { get; set; } = new();

    private IReadOnlyList<CurrencyView> _currencies = [];
    private bool _isManager;
    private string _zoneId = TimeZoneService.Utc;
    private GuildQuestSettings _settings = new();
    private IReadOnlyList<QuestType> _types = [];
    private FeatureVerdict _gate;

    protected override async Task LoadAsync()
    {
        _gate = await Gate.EvaluateAsync(GuildId, PlatformFeature.Quests);
        if (!_gate.IsEnabled)
        {
            return; // posting is blocked when quests are off
        }

        _isManager = await Auth.IsQuestManagerAsync(GuildId, UserId);
        _zoneId = await TimeZones.ResolveZoneIdAsync(GuildId, UserId);
        _currencies = (await CurrencyAdmin.ListAsync(GuildId)).Where(c => c.IsSpendable).ToList();
        _settings = await Reads.GetSettingsAsync(GuildId);
        _types = await Reads.GetQuestTypesAsync(GuildId);
    }

    private async Task PostAsync()
    {
        // Guild quests are manager-only and carry tier + slots; player quests are single-taker and tiered at intake.
        var f = Post.Fields;
        var isGuild = _isManager && Post.Kind == "guild";
        var origin = isGuild ? QuestOrigin.Guild : QuestOrigin.Player;
        var tier = isGuild ? f.Tier : QuestTier.None;
        var capacity = isGuild ? Math.Max(1, f.Capacity) : 1;

        var startsAt = await TimeZones.LocalToUtcAsync(GuildId, UserId, f.StartsAt);
        var deadline = await TimeZones.LocalToUtcAsync(GuildId, UserId, f.ExpiresAt);

        var command = new PostQuest(GuildId, UserId, origin, f.Name ?? "", f.Currency ?? "POINTS", f.Amount,
            f.Details ?? "", startsAt, deadline, tier, Post.RequestFinalApproval, capacity, f.TypeId);
        var result = (await Bus.InvokeAsync<Result<Guid>>(command)).ToCommandResult($"Quest **{f.Name}** posted.");
        Message = result.Message;
        if (!result.IsError)
        {
            Post = new PostInput();
        }

        await LoadAsync();
    }

    public class PostInput
    {
        public string Kind { get; set; } = "personal";
        public bool RequestFinalApproval { get; set; }
        public QuestFormModel Fields { get; set; } = new();
    }
}
