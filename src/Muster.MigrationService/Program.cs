using JasperFx;
using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure;
using Muster.Persistence;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddMusterInfrastructure();

// Wolverine is registered here (with auto-provisioning OFF, see WolverineExtensions) so this host can expose the
// JasperFx db-* schema commands for the message store (muster.wolverine_*): db-dump / db-apply / db-assert.
// No Service Bus is wired to this host, so the Wolverine bus stays in-memory — this only contributes the SQL
// store + its CLI. See docs/deployment.md "Wolverine message-store schema".
builder.AddMusterMessaging(hostName: "migrations");

var host = builder.Build();

// JasperFx schema CLI path: `dotnet run --project src/Muster.MigrationService -- db-dump <file>` (export),
// `-- db-apply` (apply), `-- db-assert` (verify). Used to manage the Wolverine store schema out of band — see
// the deployment docs. Any db-* argument routes to JasperFx and returns its exit code.
if (args.Length > 0 && args[0].StartsWith("db-", StringComparison.OrdinalIgnoreCase))
{
    return await host.RunJasperFxCommands(args);
}

// Default (the deploy-time Container App Job): apply EF Core migrations to the application schema, then exit.
// The host is never started — Wolverine never runs, never provisions. The Wolverine message-store schema is
// applied separately via the db-* commands above (one-time per env / on Wolverine version bumps).
var logger = host.Services.GetRequiredService<ILogger<Program>>();
try
{
    using var scope = host.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();
    // A fresh DB on a low SQL tier can exceed EF's 30s default command timeout; this host is run-once.
    db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
    logger.LogInformation("Applying EF Core migrations...");
    await db.Database.MigrateAsync();
    logger.LogInformation("EF Core migrations applied.");

    // Forward-migrate quest settings out of the legacy owned JSON into the new GuildQuestSettings table for every
    // guild that doesn't have a row yet (the AddGuildQuestSettings cutover). Idempotent — a no-op once seeded.
    var seeded = await Muster.Infrastructure.Services.Quests.GuildQuestSettingsService.BackfillAsync(db);
    logger.LogInformation("Quest settings backfill: {Count} guild row(s) seeded.", seeded);
    return 0;
}
catch (Exception ex)
{
    logger.LogError(ex, "Database migration failed.");
    return 1; // Non-zero fails the Container App Job, halting the deploy before web/bot revisions roll.
}
