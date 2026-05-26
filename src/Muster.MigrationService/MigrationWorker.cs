using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Infrastructure;

namespace Muster.MigrationService;

/// <summary>
/// Run-once worker that applies pending EF Core migrations on deploy, then stops the host. Replaces
/// any blind auto-migrate in the bot/web; in Azure this runs as a one-off Container Apps job.
/// </summary>
public class MigrationWorker(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    ILogger<MigrationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();

            logger.LogInformation("Applying database migrations...");
            await db.Database.MigrateAsync(stoppingToken);
            logger.LogInformation("Database migrations applied.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database migration failed.");
            throw;
        }

        lifetime.StopApplication();
    }
}
