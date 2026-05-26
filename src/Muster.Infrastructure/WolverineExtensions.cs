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
    public static TBuilder AddMusterMessaging<TBuilder>(this TBuilder builder, bool listenForQuestBoard = false, bool listenForCurrencyEvents = false, string connectionName = "musterdb")
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

                // Currency movements drive DM receipts (grant received / staff mint+adjust), which only the Bot can
                // deliver. Route them over a durable SQL queue so a grant from any origin reaches the bot; only the
                // bot listens + handles (CurrencyDmHandler in the bot assembly). Pruning checkpoints bypass StageAsync,
                // so the firehose excludes them.
                opts.PublishMessage<CurrencyMovementRecorded>().ToSqlServerQueue(CurrencyEventsQueue);
                if (listenForCurrencyEvents)
                {
                    opts.ListenToSqlServerQueue(CurrencyEventsQueue);
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
