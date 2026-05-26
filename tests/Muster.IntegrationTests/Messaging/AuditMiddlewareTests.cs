using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Muster.Contracts;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Muster.Persistence;
using Muster.Infrastructure.Services.Ledger;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Quests;
using Wolverine;
using Xunit;

namespace Muster.IntegrationTests;

/// <summary>
/// Drives a real Wolverine host so the audit middleware actually weaves and fires (handler-direct tests
/// bypass it). Proves: a guild command invoked via the bus runs the handler AND records exactly one audit row.
/// </summary>
public class AuditMiddlewareTests
{
    [Fact]
    public async Task ApproveCommand_ViaBus_RunsHandler_AndAuditsOnce()
    {
        var dbName = $"audit-mw-{Guid.NewGuid()}"; // one shared in-memory store across all scopes
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDbContext<MusterDbContext>(o => o.UseInMemoryDatabase(dbName));
        builder.Services.AddScoped<GuildAuthorizationService>();
        builder.Services.AddScoped<ICurrencyEventSink, NullCurrencyEventSink>();
        builder.Services.AddScoped<ICurrencyService, CurrencyService>();
        builder.Services.AddScoped<IQuestService, QuestService>();
        builder.Services.AddScoped<IQuestAuthorizer, QuestAuthorizer>();
        builder.Services.AddScoped<AuditService>();
        builder.AddMusterMessaging();

        using var host = builder.Build();
        await host.StartAsync();

        Guid questId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();
            await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1); // owner 1 ⇒ GuildMaster
            var points = await db.Currencies.SingleAsync(c => c.Code == "POINTS");
            var quests = scope.ServiceProvider.GetRequiredService<IQuestService>();
            var quest = (await quests.PostQuestAsync(new QuestDraft(1, 1, QuestOrigin.Guild, "Recruit", "", points.Id, 100))).Quest!;
            await quests.ClaimAsync(quest.Id, 10);
            await quests.SubmitAsync(quest.Id, 10);
            questId = quest.Id;
        }

        var result = await host.Services.GetRequiredService<IMessageBus>()
            .InvokeAsync<Result>(new ApproveQuestSubmission(1, questId, MemberId: 10, ReviewerId: 1));
        Assert.True(result.Ok, $"status={result.Status}");

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();
            Assert.Equal(100, (await db.Wallets.SingleAsync(w => w.UserId == 10)).Balance); // handler ran
            var audits = await db.AuditLogs.Where(a => a.Action == nameof(ApproveQuestSubmission)).ToListAsync();
            Assert.Single(audits); // middleware recorded exactly once
            Assert.Equal(1ul, audits[0].ActorUserId); // actor = ReviewerId
        }

        await host.StopAsync();
    }
}
