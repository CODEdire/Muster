using Muster.Domain;
using Muster.Infrastructure.Services.Membership;

namespace Muster.Infrastructure.Services.Currencies;

/// <summary>An action a <see cref="GuildActor"/> may attempt against a member's wallet — the authorization unit.</summary>
public enum CurrencyPermission
{
    View,     // read a balance / ledger
    Spend,    // burn from one's own wallet
    Transfer, // move funds out of a wallet to another member
    Mint,     // create funds from nothing into a wallet (staff)
    Adjust,   // correct a balance up or down (staff)
}

/// <summary>
/// Resource-based authorization for currency, mirroring <c>IQuestAuthorizer</c>: the single place the
/// owner-vs-staff rules live so command handlers (before mutating) and the UI (button visibility) can't drift.
/// The "resource" is the <b>subject</b> — the member whose wallet the action targets. Members act only on their
/// own wallet (spend / transfer-out / view); economy staff (officers + admins) mint, adjust, and may move or
/// view anyone's. The escrow/house account (<see cref="CurrencyService.EscrowAccountUserId"/>) is staff-only.
/// </summary>
public interface ICurrencyAuthorizer
{
    Task<bool> AuthorizeAsync(GuildActor actor, ulong subjectUserId, CurrencyPermission action, CancellationToken ct = default);

    /// <summary>The pure rule, with the staff flag already resolved — used by the UI for button visibility.</summary>
    bool Allows(GuildActor actor, bool isManager, ulong subjectUserId, CurrencyPermission action);
}

public sealed class CurrencyAuthorizer(GuildAuthorizationService roles) : ICurrencyAuthorizer
{
    public async Task<bool> AuthorizeAsync(GuildActor actor, ulong subjectUserId, CurrencyPermission action, CancellationToken ct = default)
    {
        // EconomyManager (or admin) is the economy-management tier — mint/adjust + acting on others. Legacy
        // OfficerRoleIds still flow through because IsEconomyManagerAsync treats them as an alias.
        var isManager = await roles.IsEconomyManagerAsync(actor.GuildId, actor.UserId, ct);
        return Allows(actor, isManager, subjectUserId, action);
    }

    public bool Allows(GuildActor actor, bool isManager, ulong subjectUserId, CurrencyPermission action)
    {
        var isSelf = subjectUserId == actor.UserId && subjectUserId != CurrencyService.EscrowAccountUserId;

        return action switch
        {
            // Staff-only: minting and balance corrections.
            CurrencyPermission.Mint or CurrencyPermission.Adjust => isManager,

            // Self-service for one's own wallet; staff may move/view anyone's.
            CurrencyPermission.View or CurrencyPermission.Transfer => isSelf || isManager,
            CurrencyPermission.Spend => isSelf || isManager,

            _ => false,
        };
    }
}
