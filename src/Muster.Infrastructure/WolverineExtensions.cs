using System.Reflection;
using JasperFx.Resources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Muster.Contracts;
using Muster.Infrastructure.Messaging;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;

namespace Muster.Infrastructure;

public static class WolverineExtensions
{
    /// <summary>Durable SQL queue the quest lifecycle events flow through to reach the bot's channel board.</summary>
    public const string QuestBoardQueue = "quest-board";

    /// <summary>Durable SQL queue currency movements flow through to reach the bot's DM receipts (a grant from
    /// web/API still notifies the recipient).</summary>
    public const string CurrencyEventsQueue = "currency-events";

    /// <summary>Durable SQL queue a web-triggered "sync members from Discord" flows through to reach the bot (only it
    /// holds the gateway client to pull the roster).</summary>
    public const string MemberSyncQueue = "member-sync";

    /// <summary>Durable SQL queue live session-attendance changes flow through to reach the web's push-updated
    /// session views. Mostly published by the bot (voice reconcile); only the web listens + fans out to circuits.</summary>
    public const string SessionEventsQueue = "session-events";

    /// <summary>Durable SQL queue that carries a <b>copy</b> of every quest lifecycle event to the web's push-updated
    /// quest views. The same <c>QuestLifecycleNotified</c> already feeds the bot's board on <see cref="QuestBoardQueue"/>;
    /// routing it here too (publisher fan-out — SQL transport has no topic/subscription) gives the web its own copy so
    /// it never competes with the bot. Only the web listens + fans out to circuits.</summary>
    public const string QuestViewsQueue = "quest-views";

    /// <summary>
    /// Configures the Wolverine command/query bus and discovers handlers in the Infrastructure assembly.
    /// When a SQL connection is available it enables the durable outbox/inbox (EF Core + SQL Server) so
    /// ledger writes and the messages they cascade commit together; otherwise it runs in-memory (local
    /// dev without a database). No external broker in v1 — contracts are broker-agnostic so Azure Service
    /// Bus can be enabled later without handler changes.
    ///
    /// <paramref name="listenForQuestBoard"/> and <paramref name="listenForCurrencyEvents"/> are set only by the
    /// <b>Bot</b> host: quest lifecycle events and currency movements are routed over durable SQL queues (so a change
    /// from web/API/the sweep still reaches the bot), and only the bot listens on them + renders the Discord channel
    /// board / sends DM receipts.
    /// </summary>
    public static TBuilder AddMusterMessaging<TBuilder>(this TBuilder builder, bool listenForQuestBoard = false, bool listenForCurrencyEvents = false, bool listenForMemberSync = false, bool listenForSessionEvents = false, bool listenForQuestViews = false, string connectionName = "musterdb")
        where TBuilder : IHostApplicationBuilder
    {
        var connectionString = builder.Configuration.GetConnectionString(connectionName);

        // AddMusterMessaging lives in Infrastructure, so Wolverine would otherwise infer the application
        // assembly as Infrastructure and miss the host's Wolverine.HTTP endpoints (in Muster.Web). Point
        // it at the host's entry assembly and explicitly include Infrastructure for message handlers.
        var hostAssembly = Assembly.GetEntryAssembly() ?? typeof(WolverineExtensions).Assembly;

        builder.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = hostAssembly;
            opts.Discovery.IncludeAssembly(hostAssembly);
            opts.Discovery.IncludeAssembly(typeof(WolverineExtensions).Assembly);

            // Audit every successful guild command in one place — attached only to IGuildCommand handler chains.
            opts.Policies.AddMiddleware(typeof(AuditMiddleware),
                chain => typeof(Muster.Contracts.IGuildCommand).IsAssignableFrom(chain.MessageType));

            // EF integration is always on so Wolverine can inline the DbContext into handler codegen (and
            // enlist SaveChanges in the outbox). The durable SQL message store is added only when a connection
            // is available; without one (local dev / tests) it runs in-memory.
            opts.UseEntityFrameworkCoreTransactions();
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                // Persistence *and transport*: the durable message store (outbox/inbox) plus the SQL Server
                // queue transport that `ToSqlServerQueue`/`ListenToSqlServerQueue` below require. (Plain
                // `PersistMessagesWithSqlServer` only sets up the store, so the queue lookup would find no transport.)
                opts.UseSqlServerPersistenceAndTransport(connectionString, "muster");

                // Quest lifecycle events drive the Discord channel board, which only the Bot host can render
                // (it owns the gateway/REST client). Route them over a durable SQL queue so a quest changed from
                // any origin — web, API, bot, or the auto-resolve sweep — reliably reaches the bot. Publishing is
                // configured in every host; only the bot listens + handles (the handler lives in the bot assembly).
                opts.PublishMessage<QuestLifecycleNotified>().ToSqlServerQueue(QuestBoardQueue);
                if (listenForQuestBoard)
                {
                    opts.ListenToSqlServerQueue(QuestBoardQueue);
                }

                // A second copy of every quest lifecycle event goes to the web's push-updated quest board/detail.
                // Routing the same message to a second queue is publisher fan-out (the web gets its own copy and
                // never competes with the bot's board listener). Only the web listens here.
                opts.PublishMessage<QuestLifecycleNotified>().ToSqlServerQueue(QuestViewsQueue);
                if (listenForQuestViews)
                {
                    opts.ListenToSqlServerQueue(QuestViewsQueue);
                }

                // Currency movements drive DM receipts (grant received / staff mint+adjust), which only the Bot can
                // deliver. Route them over a durable SQL queue so a grant from any origin reaches the bot; only the
                // bot listens + handles (CurrencyDmHandler in the bot assembly). Pruning checkpoints bypass StageAsync,
                // so the firehose excludes them.
                opts.PublishMessage<CurrencyMovementRecorded>().ToSqlServerQueue(CurrencyEventsQueue);
                if (listenForCurrencyEvents)
                {
                    opts.ListenToSqlServerQueue(CurrencyEventsQueue);
                }

                // Web-triggered member sync runs on the bot (it owns the gateway client to pull the roster from Discord).
                opts.PublishMessage<SyncGuildMembers>().ToSqlServerQueue(MemberSyncQueue);
                if (listenForMemberSync)
                {
                    opts.ListenToSqlServerQueue(MemberSyncQueue);
                }

                // Live session-attendance changes drive the web's push-updated session views. The mutation usually
                // happens in the bot (voice reconcile) while the Blazor circuit lives in the web, so route over a
                // durable SQL queue. Publishing is configured in every host; only the web listens + handles (the
                // handler fans out to connected circuits via ISessionUpdateNotifier).
                opts.PublishMessage<SessionAttendanceChanged>().ToSqlServerQueue(SessionEventsQueue);
                if (listenForSessionEvents)
                {
                    opts.ListenToSqlServerQueue(SessionEventsQueue);
                }
            }
        });

        // Let Wolverine create its SQL message-store + transport-queue tables on startup (idempotent).
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            builder.Services.AddResourceSetupOnStartup();
        }

        return builder;
    }
}
