using Microsoft.AspNetCore.Components;
using Muster.Contracts;
using Muster.Domain.Entities.Guilds;
using Muster.Domain.Entities.Quests;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Commands.Quests;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Quests;
using Muster.Web.Components.Pages.Quests.Models;

namespace Muster.Web.Components.Pages.Quests;

public partial class QuestEdit
{
    [Parameter] public string QuestId { get; set; } = "";
    [SupplyParameterFromForm(FormName = "edit-quest")] private EditInput Edit { get; set; } = new();

    private QuestEditView? _mission;
    private GuildQuestSettings _settings = new();
    private IReadOnlyList<QuestType> _types = [];
    private FeatureVerdict _gate;
    private bool _isGuild;

    protected override async Task LoadAsync()
    {
        // Editing an existing quest stays reachable during wind-down (CanEnable); only a platform/plan block hides it.
        _gate = await Gate.EvaluateAsync(GuildId, PlatformFeature.Quests);
        if (!_gate.CanEnable)
        {
            return;
        }

        if (!Guid.TryParse(QuestId, out var id))
        {
            return;
        }

        _settings = await Reads.GetSettingsAsync(GuildId);
        _types = await Reads.GetQuestTypesAsync(GuildId);
        _mission = await Reads.GetForEditAsync(GuildId, id);
        if (_mission is null)
        {
            return;
        }

        _isGuild = _mission.Origin == QuestOrigin.Guild;
        if (string.IsNullOrEmpty(Edit.Submitted))
        {
            Edit.Fields.Name = _mission.Name;
            Edit.Fields.Details = _mission.Description;
            Edit.Fields.Amount = _mission.RewardAmount;
            Edit.Fields.Tier = _mission.Tier;
            Edit.Fields.Capacity = _mission.Capacity;
            Edit.Fields.ExpiresAt = _mission.Deadline is { } d ? d.UtcDateTime : null;
            Edit.Fields.QuestType = _mission.QuestTypeId?.ToString();
        }
    }

    private async Task SaveAsync()
    {
        if (!Guid.TryParse(QuestId, out var id))
        {
            return;
        }

        var f = Edit.Fields;
        var deadline = await TimeZones.LocalToUtcAsync(GuildId, UserId, f.ExpiresAt);
        var command = new EditQuest(GuildId, id, UserId, f.Name, f.Details,
            _isGuild ? f.Amount : null, deadline, _isGuild ? f.Tier : null, _isGuild ? f.Capacity : null,
            f.TypeId ?? Guid.Empty); // empty = clear the type when "— none —" is picked
        Message = (await Bus.InvokeAsync<Result>(command)).ToCommandResult("Quest updated.").Message;
        await LoadAsync();
    }

    public class EditInput
    {
        public string? Submitted { get; set; }
        public QuestFormModel Fields { get; set; } = new();
    }
}
