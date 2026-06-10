using System.Reflection;

namespace Muster.Infrastructure.Services.Platform;

/// <summary>The functional grouping of an audit action — used for filtering the log and grouping analytics.
/// <see cref="Other"/> is the fallback for actions not registered in <see cref="AuditActions"/>.</summary>
public enum AuditCategory
{
    Other = 0,
    Configuration,
    Currency,
    Tracking,
    Quests,
    Seasons,
    Membership,
    ApiClients,
    Shop,
    System,
}

/// <summary>A registered audit action: stable string key (stored as-is on every row), the functional
/// <see cref="Category"/>, and a free-form <see cref="Area"/> label naming the subsystem within the category
/// (e.g. "Role mapping", "Session", "Webhook"). Implicitly converts to its key so call sites can drop it anywhere
/// a string is expected. Distinct from <c>AuditOrigin</c>, which is where the entry was triggered from.</summary>
public readonly record struct AuditAction(string Key, AuditCategory Category, string Area)
{
    public static implicit operator string(AuditAction a) => a.Key;
    public override string ToString() => Key;
}

/// <summary>
/// The single source of truth for every audit action key emitted by the system. New actions should be added here
/// rather than written inline as magic strings — the registry resolver (<see cref="AuditActionMetadata"/>) reads
/// these via reflection so categories/sources stay aligned with what we actually emit.
/// </summary>
public static class AuditActions
{
    public static class Config
    {
        public static readonly AuditAction AdminRole = new("config.adminRole", AuditCategory.Configuration, "Role mapping");
        public static readonly AuditAction ParticipantRole = new("config.participantRole", AuditCategory.Configuration, "Role mapping");
        public static readonly AuditAction QuestManagerRole = new("config.questmanagerRole", AuditCategory.Configuration, "Role mapping");
        public static readonly AuditAction EconomyManagerRole = new("config.economyRole", AuditCategory.Configuration, "Role mapping");
        public static readonly AuditAction TrackingManagerRole = new("config.trackingRole", AuditCategory.Configuration, "Role mapping");
        public static readonly AuditAction AuditorRole = new("config.auditorRole", AuditCategory.Configuration, "Role mapping");
        public static readonly AuditAction LedgerRetention = new("config.ledgerRetention", AuditCategory.Configuration, "Currency");
        public static readonly AuditAction TrackingSettings = new("config.tracking", AuditCategory.Configuration, "Tracking");
        public static readonly AuditAction TrackChannel = new("config.trackChannel", AuditCategory.Configuration, "Tracking");
        public static readonly AuditAction UntrackChannel = new("config.untrackChannel", AuditCategory.Configuration, "Tracking");
        public static readonly AuditAction QuestChannel = new("config.questChannel", AuditCategory.Configuration, "Quests");
        public static readonly AuditAction QuestAutomation = new("config.questAutomation", AuditCategory.Configuration, "Quests");
        public static readonly AuditAction QuestApproval = new("config.questApproval", AuditCategory.Configuration, "Quests");
        public static readonly AuditAction QuestTierPoints = new("config.questTierPoints", AuditCategory.Configuration, "Quests");
        public static readonly AuditAction MultiplierToggle = new("config.multiplier.toggle", AuditCategory.Configuration, "Multipliers");
        public static readonly AuditAction MultiplierAdd = new("config.multiplier.add", AuditCategory.Configuration, "Multipliers");
        public static readonly AuditAction MultiplierRemove = new("config.multiplier.remove", AuditCategory.Configuration, "Multipliers");

        /// <summary>Map the role-toggle "kind" string from the role-mapping form to the typed action.</summary>
        public static AuditAction RoleByKind(string kind) => kind switch
        {
            "participant" => ParticipantRole,
            "questmanager" => QuestManagerRole,
            "economy" => EconomyManagerRole,
            "tracking" => TrackingManagerRole,
            "auditor" => AuditorRole,
            _ => AdminRole,
        };
    }

    public static class Currency
    {
        public static readonly AuditAction RebuildWallets = new("currency.rebuildWallets", AuditCategory.Currency, "Wallet");
        public static readonly AuditAction Create = new("currency.create", AuditCategory.Currency, "Definition");
        public static readonly AuditAction Update = new("currency.update", AuditCategory.Currency, "Definition");
        public static readonly AuditAction SyncAll = new("currency.syncAll", AuditCategory.Currency, "Sync");
        public static readonly AuditAction Connector = new("currency.connector", AuditCategory.Currency, "Connector");
        // External connector activity (outbound add/remove call, periodic sweep summary, anomalous drift on reconcile).
        public static readonly AuditAction ConnectorPush = new("currency.connectorPush", AuditCategory.Currency, "Connector");
        public static readonly AuditAction SyncSweep = new("currency.syncSweep", AuditCategory.Currency, "Sync");
        public static readonly AuditAction DriftAnomaly = new("currency.driftAnomaly", AuditCategory.Currency, "Sync");
        public static readonly AuditAction BulkQueue = new("currency.bulkQueue", AuditCategory.Currency, "Bulk");
        public static readonly AuditAction WebhookCreate = new("currency.webhookCreate", AuditCategory.Currency, "Webhook");
        public static readonly AuditAction WebhookToggle = new("currency.webhookToggle", AuditCategory.Currency, "Webhook");
        public static readonly AuditAction WebhookDelete = new("currency.webhookDelete", AuditCategory.Currency, "Webhook");
        // Movement commands recorded by the audit middleware on the way out of every successful guild command.
        public static readonly AuditAction Adjust = new("currency.adjust", AuditCategory.Currency, "Adjustment");
        public static readonly AuditAction Transfer = new("currency.transfer", AuditCategory.Currency, "Transfer");
        public static readonly AuditAction Mint = new("currency.mint", AuditCategory.Currency, "Mint");
        public static readonly AuditAction Spend = new("currency.spend", AuditCategory.Currency, "Spend");
    }

    public static class Track
    {
        public static readonly AuditAction SessionStart = new("track.session.start", AuditCategory.Tracking, "Session");
        public static readonly AuditAction SessionStop = new("track.session.stop", AuditCategory.Tracking, "Session");

        /// <summary>System actor (<c>actorUserId = 0</c>) — a Discord scheduled event entered Active and the
        /// gateway handler auto-opened a tracking session. Lets admins trace "where did this session come from?"
        /// without a human in the audit row.</summary>
        public static readonly AuditAction ScheduledEventSessionOpen =
            new("track.session.scheduledEventOpen", AuditCategory.Tracking, "Session");

        /// <summary>System actor — scheduled event ended / cancelled and the gateway handler auto-closed the
        /// matching tracking session.</summary>
        public static readonly AuditAction ScheduledEventSessionClose =
            new("track.session.scheduledEventClose", AuditCategory.Tracking, "Session");
    }

    public static class Muster
    {
        // Muster (reaction check-in) lifecycle. Filed under the Tracking category — musters are managed by the
        // Tracking Manager role and gate session coin — with a "Muster" source so they group on the audit page.
        public static readonly AuditAction Create = new("muster.create", AuditCategory.Tracking, "Muster");
        public static readonly AuditAction CheckIn = new("muster.checkin", AuditCategory.Tracking, "Muster");
        public static readonly AuditAction AddParticipant = new("muster.participant.add", AuditCategory.Tracking, "Muster");
        public static readonly AuditAction RemoveParticipant = new("muster.participant.remove", AuditCategory.Tracking, "Muster");
        public static readonly AuditAction Close = new("muster.close", AuditCategory.Tracking, "Muster");
        public static readonly AuditAction Link = new("muster.session.link", AuditCategory.Tracking, "Muster");
        public static readonly AuditAction Unlink = new("muster.session.unlink", AuditCategory.Tracking, "Muster");
        public static readonly AuditAction SetGate = new("muster.session.gate", AuditCategory.Tracking, "Muster");
        public static readonly AuditAction CardRemove = new("muster.card.remove", AuditCategory.Tracking, "Muster");
    }

    public static class Quests
    {
        // Lifecycle actions — every IGuildCommand the audit middleware sees flows into one of these.
        public static readonly AuditAction Post = new("quest.post", AuditCategory.Quests, "Lifecycle");
        public static readonly AuditAction Edit = new("quest.edit", AuditCategory.Quests, "Lifecycle");
        public static readonly AuditAction Cancel = new("quest.cancel", AuditCategory.Quests, "Lifecycle");
        public static readonly AuditAction Claim = new("quest.claim", AuditCategory.Quests, "Lifecycle");
        public static readonly AuditAction Submit = new("quest.submit", AuditCategory.Quests, "Lifecycle");
        public static readonly AuditAction Approve = new("quest.approve", AuditCategory.Quests, "Review");
        public static readonly AuditAction Reject = new("quest.reject", AuditCategory.Quests, "Review");
        public static readonly AuditAction ReopenRejection = new("quest.reopenRejection", AuditCategory.Quests, "Review");
        public static readonly AuditAction Confirm = new("quest.confirm", AuditCategory.Quests, "Review");
        public static readonly AuditAction Dispute = new("quest.dispute", AuditCategory.Quests, "Review");
        public static readonly AuditAction Arbitrate = new("quest.arbitrate", AuditCategory.Quests, "Review");
        public static readonly AuditAction RequestRevision = new("quest.requestRevision", AuditCategory.Quests, "Review");
        public static readonly AuditAction AcceptIntake = new("quest.acceptIntake", AuditCategory.Quests, "Intake");
        public static readonly AuditAction RejectIntake = new("quest.rejectIntake", AuditCategory.Quests, "Intake");
        public static readonly AuditAction Finalize = new("quest.finalize", AuditCategory.Quests, "Review");
        public static readonly AuditAction ReleaseClaim = new("quest.releaseClaim", AuditCategory.Quests, "Lifecycle");

        public static readonly AuditAction AutoClaimReleased = new("quest.auto.claim.released", AuditCategory.Quests, "Automation");

        /// <summary>The auto-resolve sweep records one of several outcomes per stale-resolution sweep — the suffix is
        /// the configured enum (Approve/Reject/Dispute/Accept/…). Kept dynamic since the suffix is settings-driven.</summary>
        public static AuditAction AutoIntake(string outcome) =>
            new($"quest.auto.intake.{outcome}", AuditCategory.Quests, "Automation");

        public static AuditAction AutoSubmission(string outcome) =>
            new($"quest.auto.submission.{outcome}", AuditCategory.Quests, "Automation");

        public static AuditAction AutoFinal(string outcome) =>
            new($"quest.auto.final.{outcome}", AuditCategory.Quests, "Automation");

        public static AuditAction AutoDispute(string outcome) =>
            new($"quest.auto.dispute.{outcome}", AuditCategory.Quests, "Automation");
    }

    public static class Seasons
    {
        public static readonly AuditAction Start = new("season.start", AuditCategory.Seasons, "Season");
        public static readonly AuditAction End = new("season.end", AuditCategory.Seasons, "Season");
    }

    public static class Members
    {
        public static readonly AuditAction SyncRequested = new("members.syncRequested", AuditCategory.Membership, "Sync");
    }

    public static class Api
    {
        public static readonly AuditAction ClientCreate = new("apiclient.create", AuditCategory.ApiClients, "API client");
        public static readonly AuditAction ClientRevoke = new("apiclient.revoke", AuditCategory.ApiClients, "API client");
    }

    public static class Shop
    {
        // Buyer / seller / manager order actions.
        public static readonly AuditAction Purchase = new("shop.purchase", AuditCategory.Shop, "Order");
        public static readonly AuditAction MarkDelivered = new("shop.deliver", AuditCategory.Shop, "Order");
        public static readonly AuditAction Confirm = new("shop.confirm", AuditCategory.Shop, "Order");
        public static readonly AuditAction Cancel = new("shop.cancel", AuditCategory.Shop, "Order");
        public static readonly AuditAction Dispute = new("shop.dispute", AuditCategory.Shop, "Dispute");
        public static readonly AuditAction Arbitrate = new("shop.arbitrate", AuditCategory.Shop, "Dispute");
        public static readonly AuditAction Rate = new("shop.rate", AuditCategory.Shop, "Rating");
        // Offer negotiation.
        public static readonly AuditAction Offer = new("shop.offer", AuditCategory.Shop, "Offer");
        public static readonly AuditAction OfferAccept = new("shop.offerAccept", AuditCategory.Shop, "Offer");
        public static readonly AuditAction OfferCounter = new("shop.offerCounter", AuditCategory.Shop, "Offer");
        public static readonly AuditAction OfferEnd = new("shop.offerEnd", AuditCategory.Shop, "Offer");
        // System sweeps (no human actor) — the gap the command middleware can't cover.
        public static readonly AuditAction AutoSettle = new("shop.autoSettle", AuditCategory.Shop, "Sweep");
        public static readonly AuditAction AutoCancelUndelivered = new("shop.autoCancel", AuditCategory.Shop, "Sweep");
        public static readonly AuditAction AutoResolveDispute = new("shop.autoResolve", AuditCategory.Shop, "Sweep");
        public static readonly AuditAction OfferExpired = new("shop.offerExpired", AuditCategory.Shop, "Sweep");
    }

    public static class System
    {
        /// <summary>Catch-all for the audit middleware. Wraps a command type name with the System category so it
        /// still groups correctly even though the key is whatever .NET type the command happens to be.</summary>
        public static AuditAction Command(string typeName) =>
            new(typeName, AuditCategory.System, "Command");
    }
}

/// <summary>Reverse-index resolver — given a stored action key, looks up its <see cref="AuditAction"/> metadata
/// (Category + Source). Falls back to <see cref="AuditCategory.Other"/> with the raw key for unknown actions
/// (legacy rows, ad-hoc strings still in transit). Built once via reflection over <see cref="AuditActions"/>.</summary>
public static class AuditActionMetadata
{
    private static readonly Dictionary<string, AuditAction> _byKey = Build();

    public static AuditAction Resolve(string key) =>
        _byKey.TryGetValue(key, out var a) ? a : new AuditAction(key, AuditCategory.Other, "");

    /// <summary>Every registered action — used by UI category filters and category-summary screens.</summary>
    public static IReadOnlyCollection<AuditAction> All => _byKey.Values;

    private static Dictionary<string, AuditAction> Build()
    {
        var dict = new Dictionary<string, AuditAction>(StringComparer.Ordinal);
        foreach (var type in typeof(AuditActions).GetNestedTypes(BindingFlags.Public))
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType == typeof(AuditAction) && field.GetValue(null) is AuditAction a)
                {
                    dict[a.Key] = a;
                }
            }
        }
        return dict;
    }
}
