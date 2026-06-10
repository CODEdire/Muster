using System.Text;

namespace Muster.Contracts;

/// <summary>
/// Single source of truth for the cross-host Service Bus topology. Each entry pairs a contract type with
/// the hosts that should receive it; both the AppHost (provisioning) and Wolverine (publish/listen wiring)
/// iterate this list, so the names land identically on both sides.
///
/// <para>
/// Naming convention: PascalCase contract name → kebab-case topic name
/// (<c>QuestLifecycleNotified</c> → <c>quest-lifecycle-notified</c>; resolves to
/// <c>muster.quest-lifecycle-notified</c> after Wolverine's transport prefix).
/// </para>
/// </summary>
public static class MessageRouting
{
    /// <summary>A cross-host contract and the hosts whose handlers should receive it.</summary>
    public sealed record Contract(Type MessageType, IReadOnlyList<string> SubscribingHosts);

    /// <summary>Closed set of contracts that travel cross-host. Adding a new entry takes care of both
    /// AppHost provisioning (topic + per-host sub) and Wolverine routing (publish + listen) automatically.</summary>
    public static readonly IReadOnlyList<Contract> Contracts =
    [
        // Bot renders the Discord channel board; web pushes the same change into the quest views circuit.
        new(typeof(QuestLifecycleNotified), [HostNames.Bot, HostNames.Web]),
        // Bot renders the muster card from any host's change (slash, button, or web authoring); web fans the
        // same change out to connected Blazor circuits so the detail/board reflect check-ins live.
        new(typeof(MusterChanged), [HostNames.Bot, HostNames.Web]),
        // Bot deletes the Discord card on a manual "remove from channel" request (web → bot).
        new(typeof(RemoveMusterCard), [HostNames.Bot]),
        // Bot delivers DM receipts (CurrencyDmHandler) and HMAC-signed webhooks (CurrencyWebhookHandler).
        new(typeof(CurrencyMovementRecorded), [HostNames.Bot]),
        // Bot posts shop dispute alerts + maintains the channel's featured/home cards from any host's change.
        new(typeof(ShopLifecycleNotified), [HostNames.Bot]),
        // Bot renders/removes a store's home card on store create/edit/delete.
        new(typeof(ShopStoreChanged), [HostNames.Bot]),
        // Bot owns the Discord gateway client needed to pull the roster.
        new(typeof(SyncGuildMembers), [HostNames.Bot]),
        // Web fans the live attendance change out to connected Blazor circuits.
        new(typeof(SessionAttendanceChanged), [HostNames.Web]),
    ];

    /// <summary>True if the given type appears in <see cref="Contracts"/>. Used as the conventional
    /// routing predicate so the convention only claims cross-host messages — local commands
    /// (<c>IGuildCommand</c>) and discovered noise (NetCord gateway events) stay outside the transport.</summary>
    public static bool IsCrossHost(Type messageType) =>
        Contracts.Any(c => c.MessageType == messageType);

    /// <summary>Kebab-case derivation of the topic name from the message type's CLR name.</summary>
    public static string TopicName(Type messageType) => ToKebabCase(messageType.Name);

    private static string ToKebabCase(string pascalCase)
    {
        if (string.IsNullOrEmpty(pascalCase))
        {
            return pascalCase;
        }

        var sb = new StringBuilder(pascalCase.Length + 8);
        for (var i = 0; i < pascalCase.Length; i++)
        {
            var ch = pascalCase[i];
            if (char.IsUpper(ch) && i > 0)
            {
                sb.Append('-');
            }
            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }
}

/// <summary>Stable identifiers each host uses for its Wolverine <c>ServiceName</c>. The same string
/// becomes the host's subscription name on every topic it listens to, so a topic with two subscribers
/// has two clearly named subs in the portal (<c>bot</c> + <c>web</c>).</summary>
public static class HostNames
{
    public const string Bot = "bot";
    public const string Web = "web";
}
