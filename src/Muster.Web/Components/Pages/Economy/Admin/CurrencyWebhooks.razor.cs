using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Services.Platform;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;

namespace Muster.Web.Components.Pages.Economy.Admin;

public partial class CurrencyWebhooks
{
    [SupplyParameterFromForm(FormName = "create-webhook")] private WebhookInput Input { get; set; } = new();

    // Sources worth subscribing to (checkpoints never publish, so they're omitted).
    private static readonly CurrencyLedgerSource[] _sources =
    [
        CurrencyLedgerSource.TrackingSession, CurrencyLedgerSource.Quest, CurrencyLedgerSource.Muster,
        CurrencyLedgerSource.Event, CurrencyLedgerSource.Transfer, CurrencyLedgerSource.Adjustment,
        CurrencyLedgerSource.ManualAward, CurrencyLedgerSource.Connector,
    ];

    private IReadOnlyList<WebhookView> _hooks = [];

    protected override async Task LoadAsync() => _hooks = await Webhooks.ListAsync(GuildId);

    private async Task CreateAsync()
    {
        var result = await Webhooks.CreateAsync(GuildId, Input.Url ?? "", Input.Secret, Input.Sources);
        Message = result.Message;
        if (!result.IsError)
        {
            await Audit.RecordWebhookCreatedAsync(GuildId, UserId, Guid.Empty, Input.Url ?? "");
            Input = new WebhookInput();
        }

        await LoadAsync();
    }

    private async Task TestAsync(Guid id)
    {
        Message = (await Webhooks.TestSendAsync(GuildId, id)).Message;
        await LoadAsync();
    }

    private async Task ToggleAsync(Guid id, bool enabled)
    {
        var result = await Webhooks.SetEnabledAsync(GuildId, id, enabled);
        Message = result.Message;
        var hook = _hooks.FirstOrDefault(h => h.Id == id);
        await Audit.RecordWebhookToggledAsync(GuildId, UserId, id, hook?.Url ?? "", enabled);
        await LoadAsync();
    }

    private async Task DeleteAsync(Guid id)
    {
        var hook = _hooks.FirstOrDefault(h => h.Id == id);
        var result = await Webhooks.DeleteAsync(GuildId, id);
        Message = result.Message;
        await Audit.RecordWebhookDeletedTypedAsync(GuildId, UserId, id, hook?.Url ?? "");
        await LoadAsync();
    }

    public class WebhookInput
    {
        public string? Url { get; set; }
        public string? Secret { get; set; }
        public List<CurrencyLedgerSource> Sources { get; set; } = [];
    }
}
