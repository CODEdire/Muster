using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Commands.Membership;

/// <summary>Per-user preference writes that aren't tied to a guild (the currency-receipt DM toggle, future
/// account-wide settings). Keeps the UI off the persistence layer for these tiny writes.</summary>
public class MemberPreferencesCommandService(MusterDbContext db)
{
    /// <summary>Set the user's currency-receipt DM preference. Returns the new effective state.</summary>
    public async Task<CommandResult<bool>> SetCurrencyDmOptOutAsync(ulong userId, bool optOut, CancellationToken ct = default)
    {
        var applied = await db.SetCurrencyDmOptOutAsync(userId, optOut, ct);
        return CommandResult<bool>.Ok(applied, applied
            ? "Currency receipt DMs are off."
            : "Currency receipt DMs are on.");
    }
}
