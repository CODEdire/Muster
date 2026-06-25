using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Commands.Seasons;

namespace Muster.Web.Components.Pages.Economy.Admin;

public partial class SeasonsAdmin
{
    [SupplyParameterFromForm(FormName = "season-start")] private StartInput Input { get; set; } = new();
    [SupplyParameterFromForm(FormName = "season-end")] private EmptyInput EndInput { get; set; } = new();

    private string? _status;

    protected override async Task LoadAsync()
        => _status = (await Seasons.StatusAsync(GuildId)).Message;

    private async Task StartAsync()
    {
        var result = await Seasons.StartAsync(GuildId, Input.Name ?? string.Empty);
        Message = result.Message;
        if (!result.IsError && result.Value is { } info)
        {
            await Audit.RecordSeasonStartedAsync(GuildId, UserId, info.Id, info.Name);
        }

        Input = new StartInput();
        await LoadAsync();
    }

    private async Task EndAsync()
    {
        var result = await Seasons.EndAsync(GuildId);
        Message = result.Message;
        if (!result.IsError && result.Value is { } info)
        {
            await Audit.RecordSeasonEndedAsync(GuildId, UserId, info.Id, info.Name, info.OccurredAt);
        }

        await LoadAsync();
    }

    public class StartInput
    {
        public string? Name { get; set; }
    }

    public class EmptyInput
    {
    }
}
