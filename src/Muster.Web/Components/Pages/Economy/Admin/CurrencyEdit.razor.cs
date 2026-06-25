using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Membership;
using Muster.Contracts;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Wolverine;

namespace Muster.Web.Components.Pages.Economy.Admin;

public partial class CurrencyEdit
{
    // EconomyManager + Admin (implicit) — same tier as Currencies list.
    protected override GuildAccessTier RequiredAccess => GuildAccessTier.EconomyManager;

    [Parameter] public string? CurrencyId { get; set; }
    private EditModel Input { get; set; } = new();

    private bool _notFound;
    private bool _isSystem;       // the protected POINTS currency — can't be deleted
    private bool _authHasSecret;
    private bool _signHasSecret;
    private string? _lastStatus;
    private string? _lastError;
    private DateTimeOffset? _lastAttemptAt;
    private string _activeTab = "base";
    private bool _saving;
    private bool _busy; // for test/sync buttons
    private Muster.Infrastructure.Connectors.CurrencyConnectorSyncService.CurrencySyncBacklog? _backlog;

    private bool IsEdit => Guid.TryParse(CurrencyId, out _);
    private Guid EditId => Guid.TryParse(CurrencyId, out var id) ? id : Guid.Empty;
    private bool IsConnectorMode => Input.Mode is CurrencyMode.External or CurrencyMode.Hybrid;

    // Tab definitions. Connector tabs hide when Mode = Internal (nothing to configure).
    private record TabDef(string Id, string Label, string? Badge);
    private IEnumerable<TabDef> VisibleTabs
    {
        get
        {
            yield return new TabDef("base", "Basics", null);
            if (IsConnectorMode)
            {
                yield return new TabDef("auth", "Auth", Input.AuthScheme == ConnectorAuthScheme.None ? null : "on");
                yield return new TabDef("sec", "Security", Input.SignAlgorithm == ConnectorSignAlgorithm.None ? null : "on");
                yield return new TabDef("credit", "Credit", Input.CreditEnabled ? "on" : null);
                yield return new TabDef("debit", "Debit", Input.DebitEnabled ? "on" : null);
                yield return new TabDef("balance", "Get balance", Input.BalanceEnabled ? "on" : null);
            }
        }
    }

    private const string PlaceholderHelp =
        "Tokens use $name (no braces — the template stays valid JSON). Numbers ($userId $amount $guildId $deliveryId): " +
        "write them quoted (\"$amount\") and the quotes are stripped on send. Strings ($displayName $currencyCode " +
        "$reason $sourceType $occurredAt) render escaped inside the quotes.";

    private static string SecretPlaceholder(bool hasSecret) => hasSecret ? "•••• set — blank keeps it" : "(none)";

    protected override async Task LoadAsync()
    {
        if (!IsEdit)
        {
            return;
        }

        var view = await CurrencyAdmin.GetAsync(GuildId, EditId);
        if (view is null)
        {
            _notFound = true;
            return;
        }

        _isSystem = view.IsSystem;
        _authHasSecret = view.Connector.AuthHasSecret;
        _signHasSecret = view.Connector.SignHasSecret;
        _lastStatus = view.Connector.Config.LastStatus;
        _lastError = view.Connector.Config.LastError;
        _lastAttemptAt = view.Connector.Config.LastAttemptAt;
        Input = EditModel.From(view);
        await RefreshBacklogAsync();
    }

    private async Task RefreshBacklogAsync()
    {
        _backlog = IsEdit && IsConnectorMode && Input.BalanceEnabled
            ? await Sync.SyncBacklogAsync(GuildId, EditId)
            : null;
    }

    // Tab-panel writer that closes over the active kind so the shared RenderFragment can mutate the right
    // sub-record without each input being repeated three times.
    private void SetActionField(string kind, string field, string? value)
    {
        switch (kind)
        {
            case "credit":
                switch (field) { case "Method": Input.CreditMethod = value; break;
                    case "Path": Input.CreditPath = value; break;
                    case "Query": Input.CreditQuery = value; break;
                    case "ResponsePath": Input.CreditResponsePath = value; break;
                    case "Body": Input.CreditBody = value; break; }
                break;
            case "debit":
                switch (field) { case "Method": Input.DebitMethod = value; break;
                    case "Path": Input.DebitPath = value; break;
                    case "Query": Input.DebitQuery = value; break;
                    case "ResponsePath": Input.DebitResponsePath = value; break;
                    case "Body": Input.DebitBody = value; break; }
                break;
            case "balance":
                switch (field) { case "Method": Input.BalanceMethod = value; break;
                    case "Path": Input.BalancePath = value; break;
                    case "Query": Input.BalanceQuery = value; break;
                    case "ResponsePath": Input.BalanceResponsePath = value; break;
                    case "Body": Input.BalanceBody = value; break; }
                break;
        }
    }

    private void SetActionResponseFormat(string kind, string? raw)
    {
        if (!Enum.TryParse<ConnectorResponseFormat>(raw, out var fmt)) return;
        switch (kind)
        {
            case "credit": Input.CreditResponseFormat = fmt; break;
            case "debit": Input.DebitResponseFormat = fmt; break;
            case "balance": Input.BalanceResponseFormat = fmt; break;
        }
    }

    private void SetActionBodyFormat(string kind, string? raw)
    {
        if (!Enum.TryParse<ConnectorBodyFormat>(raw, out var fmt)) return;
        switch (kind)
        {
            case "credit": Input.CreditBodyFormat = fmt; break;
            case "debit": Input.DebitBodyFormat = fmt; break;
            case "balance": Input.BalanceBodyFormat = fmt; break;
        }
    }

    private async Task TestAsync()
    {
        _busy = true;
        StateHasChanged();
        try
        {
            Message = (await CurrencyAdmin.TestConnectorAsync(GuildId, EditId)).Message;
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task SyncAllAsync()
    {
        _busy = true;
        StateHasChanged();
        try
        {
            await Bus.PublishAsync(new SyncCurrencyBalances(GuildId, EditId));
            await Audit.RecordCurrencySyncAllAsync(GuildId, UserId, Input.Code ?? "");
            Message = "Balance sync started for all members — this runs in the background and can take a while for large servers. Reload to watch the pending count fall.";
        }
        finally
        {
            _busy = false;
            await RefreshBacklogAsync();
            StateHasChanged();
        }
    }

    private async Task DeleteAsync()
    {
        if (_busy || !IsEdit || _isSystem)
        {
            return;
        }

        var ok = await JS.InvokeAsync<bool>("confirm", new object?[]
        {
            $"Delete currency {Input.Code}? This permanently removes the currency and ALL balances and history for it. This cannot be undone.",
        });
        if (!ok)
        {
            return;
        }

        _busy = true;
        StateHasChanged();
        try
        {
            var result = await CurrencyAdmin.DeleteAsync(GuildId, EditId);
            if (result.IsError)
            {
                Message = result.Message;
                return;
            }

            Nav.NavigateTo($"/guilds/{GuildId}/currencies");
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task SaveAsync()
    {
        _saving = true;
        StateHasChanged();
        try
        {
            Guid currencyId;
            if (IsEdit)
            {
                var update = await CurrencyAdmin.UpdateAsync(GuildId, EditId, Input.Name ?? "", Input.IsSpendable, Input.Mode);
                if (update.IsError) { Message = update.Message; return; }

                currencyId = EditId;
                await Audit.RecordCurrencyUpdatedAsync(GuildId, UserId, Input.Code ?? "", Input.Name ?? "",
                    oldName: null, Input.IsSeasonal, Input.IsSpendable, Input.Mode);
            }
            else
            {
                var create = await CurrencyAdmin.CreateAsync(GuildId, Input.Code ?? "", Input.Name ?? "",
                    Input.IsSeasonal, Input.IsSpendable, Input.Mode);
                if (create.IsError) { Message = create.Message; return; }

                await Audit.RecordCurrencyCreatedAsync(GuildId, UserId, Input.Code ?? "", Input.Name ?? "",
                    Input.IsSeasonal, Input.IsSpendable, Input.Mode);
                var created = (await CurrencyAdmin.ListAsync(GuildId))
                    .FirstOrDefault(c => c.Code.Equals((Input.Code ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
                if (created is null) { Nav.NavigateTo($"/guilds/{GuildId}/currencies"); return; }

                currencyId = created.Id;
            }

            // Persist the connector (only meaningful for External/Hybrid; harmless otherwise).
            if (Input.Mode != CurrencyMode.Internal)
            {
                var conn = await CurrencyAdmin.SetConnectorAsync(GuildId, currencyId, Input.ToConnector());
                if (conn.IsError) { Message = conn.Message; return; }

                await Audit.RecordCurrencyConnectorAsync(GuildId, UserId, Input.Code ?? "", Input.Mode);
            }

            Nav.NavigateTo($"/guilds/{GuildId}/currencies");
        }
        finally
        {
            _saving = false;
            StateHasChanged();
        }
    }

    public class EditModel
    {
        public string? Code { get; set; }
        public string? Name { get; set; }
        public bool IsSpendable { get; set; } = true;
        public bool IsSeasonal { get; set; }
        public CurrencyMode Mode { get; set; } = CurrencyMode.Internal;

        public bool Enabled { get; set; }
        public string? BaseUrl { get; set; }
        public int TimeoutSeconds { get; set; } = 10;
        public string? SuccessCodes { get; set; }
        public string? IdempotencyHeader { get; set; }
        public string? ErrorPath { get; set; }
        public int SyncIntervalMinutes { get; set; }

        public ConnectorAuthScheme AuthScheme { get; set; } = ConnectorAuthScheme.None;
        public string? AuthUsername { get; set; }
        public string? AuthSecret { get; set; }
        public string? ApiKeyName { get; set; }
        public ApiKeyLocation ApiKeyIn { get; set; } = ApiKeyLocation.Header;

        public ConnectorSignAlgorithm SignAlgorithm { get; set; } = ConnectorSignAlgorithm.None;
        public string? SignHeader { get; set; }
        public string? SignTimestampHeader { get; set; }
        public string? SignSecret { get; set; }

        public bool CreditEnabled { get; set; }
        public string? CreditMethod { get; set; }
        public string? CreditPath { get; set; }
        public string? CreditQuery { get; set; }
        public string? CreditBody { get; set; }
        public ConnectorBodyFormat CreditBodyFormat { get; set; } = ConnectorBodyFormat.Json;
        public ConnectorResponseFormat CreditResponseFormat { get; set; } = ConnectorResponseFormat.None;
        public string? CreditResponsePath { get; set; }

        public bool DebitEnabled { get; set; }
        public string? DebitMethod { get; set; }
        public string? DebitPath { get; set; }
        public string? DebitQuery { get; set; }
        public string? DebitBody { get; set; }
        public ConnectorBodyFormat DebitBodyFormat { get; set; } = ConnectorBodyFormat.Json;
        public ConnectorResponseFormat DebitResponseFormat { get; set; } = ConnectorResponseFormat.None;
        public string? DebitResponsePath { get; set; }

        public bool BalanceEnabled { get; set; }
        public string? BalanceMethod { get; set; }
        public string? BalancePath { get; set; }
        public string? BalanceQuery { get; set; }
        public string? BalanceBody { get; set; }
        public ConnectorBodyFormat BalanceBodyFormat { get; set; } = ConnectorBodyFormat.Json;
        public ConnectorResponseFormat BalanceResponseFormat { get; set; } = ConnectorResponseFormat.Json;
        public string? BalanceResponsePath { get; set; }

        public static EditModel From(CurrencyView v)
        {
            var c = v.Connector.Config;
            return new EditModel
            {
                Code = v.Code,
                Name = v.Name,
                IsSpendable = v.IsSpendable,
                IsSeasonal = v.IsSeasonal,
                Mode = v.Mode,
                Enabled = c.Enabled,
                BaseUrl = c.BaseUrl,
                TimeoutSeconds = c.TimeoutSeconds,
                SuccessCodes = c.SuccessCodes,
                IdempotencyHeader = c.IdempotencyHeader,
                ErrorPath = c.ErrorPath,
                SyncIntervalMinutes = c.SyncIntervalMinutes,
                AuthScheme = c.Auth.Scheme,
                AuthUsername = c.Auth.Username,
                ApiKeyName = c.Auth.ApiKeyName,
                ApiKeyIn = c.Auth.ApiKeyIn,
                SignAlgorithm = c.Signing.Algorithm,
                SignHeader = c.Signing.SignatureHeader,
                SignTimestampHeader = c.Signing.TimestampHeader,
                CreditEnabled = c.Credit.Enabled,
                CreditMethod = c.Credit.Method,
                CreditPath = c.Credit.Path,
                CreditQuery = c.Credit.Query,
                CreditBody = c.Credit.BodyTemplate,
                CreditBodyFormat = c.Credit.BodyFormat,
                CreditResponseFormat = c.Credit.ResponseFormat,
                CreditResponsePath = c.Credit.ResponsePath,
                DebitEnabled = c.Debit.Enabled,
                DebitMethod = c.Debit.Method,
                DebitPath = c.Debit.Path,
                DebitQuery = c.Debit.Query,
                DebitBody = c.Debit.BodyTemplate,
                DebitBodyFormat = c.Debit.BodyFormat,
                DebitResponseFormat = c.Debit.ResponseFormat,
                DebitResponsePath = c.Debit.ResponsePath,
                BalanceEnabled = c.GetBalance.Enabled,
                BalanceMethod = c.GetBalance.Method,
                BalancePath = c.GetBalance.Path,
                BalanceQuery = c.GetBalance.Query,
                BalanceBody = c.GetBalance.BodyTemplate,
                BalanceBodyFormat = c.GetBalance.BodyFormat,
                BalanceResponseFormat = c.GetBalance.ResponseFormat,
                BalanceResponsePath = c.GetBalance.ResponsePath,
                // Secrets intentionally not prefilled (write-only).
            };
        }

        public CurrencyConnector ToConnector() => new()
        {
            Enabled = Enabled,
            BaseUrl = BaseUrl,
            TimeoutSeconds = TimeoutSeconds <= 0 ? 10 : TimeoutSeconds,
            SuccessCodes = SuccessCodes,
            IdempotencyHeader = IdempotencyHeader,
            ErrorPath = ErrorPath,
            SyncIntervalMinutes = SyncIntervalMinutes < 0 ? 0 : SyncIntervalMinutes,
            Auth = new ConnectorAuth
            {
                Scheme = AuthScheme,
                Username = AuthUsername,
                Secret = AuthSecret,
                ApiKeyName = string.IsNullOrWhiteSpace(ApiKeyName) ? "X-Api-Key" : ApiKeyName,
                ApiKeyIn = ApiKeyIn,
            },
            Signing = new ConnectorSigning
            {
                Algorithm = SignAlgorithm,
                SignatureHeader = string.IsNullOrWhiteSpace(SignHeader) ? "X-Muster-Signature" : SignHeader,
                TimestampHeader = SignTimestampHeader,
                Secret = SignSecret,
            },
            Credit = new ConnectorAction { Enabled = CreditEnabled, Method = CreditMethod ?? "POST", Path = CreditPath, Query = CreditQuery, BodyTemplate = CreditBody, BodyFormat = CreditBodyFormat, ResponseFormat = CreditResponseFormat, ResponsePath = CreditResponsePath },
            Debit = new ConnectorAction { Enabled = DebitEnabled, Method = DebitMethod ?? "POST", Path = DebitPath, Query = DebitQuery, BodyTemplate = DebitBody, BodyFormat = DebitBodyFormat, ResponseFormat = DebitResponseFormat, ResponsePath = DebitResponsePath },
            GetBalance = new ConnectorAction { Enabled = BalanceEnabled, Method = BalanceMethod ?? "GET", Path = BalancePath, Query = BalanceQuery, BodyTemplate = BalanceBody, BodyFormat = BalanceBodyFormat, ResponseFormat = BalanceResponseFormat, ResponsePath = BalanceResponsePath },
        };
    }
}
