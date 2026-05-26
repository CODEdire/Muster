var builder = DistributedApplication.CreateBuilder(args);

// Azure SQL in publish; a SQL Server container locally so `dotnet run` works without Azure.
// Locally, persist data across runs: a named data volume keeps the database when the container is
// recreated, and a persistent container lifetime keeps the same container between `dotnet run` sessions.
var sql = builder.AddAzureSqlServer("sql")
    .RunAsContainer(container => container
        .WithDataVolume()
        .WithLifetime(ContainerLifetime.Persistent));

var db = sql.AddDatabase("musterdb");

// Discord credentials are AppHost parameters -> user-secrets locally, Key Vault refs in Azure.
var discordToken = builder.AddParameter("discord-token", secret: true);
var discordClientId = builder.AddParameter("discord-clientid", secret: true);
var discordClientSecret = builder.AddParameter("discord-clientsecret", secret: true);

// Run-once migration job applies the schema before the bot/web start.
var migrations = builder.AddProject<Projects.Muster_MigrationService>("migrations")
    .WithReference(db)
    .WaitFor(db);

var web = builder.AddProject<Projects.Muster_Web>("web")
    .WithReference(db)
    .WithEnvironment("Discord__ClientId", discordClientId)
    .WithEnvironment("Discord__ClientSecret", discordClientSecret)
    // The bot token lets the web settings page list a guild's channels (for the quest-board channel picker)
    // via Discord REST — no channel table, fetched live.
    .WithEnvironment("Discord__Token", discordToken)
    .WithExternalHttpEndpoints()
    .WaitForCompletion(migrations);

// Dev tunnel exposes the local web on a public HTTPS URL so Discord's servers can fetch the quest-board embed
// images (tier badges / guild crest) and follow the title link. Anonymous so Discord needs no auth. First run
// prompts a one-time `devtunnel user login`. (In publish the web has a real public URL, so the tunnel is dev-only.)
var webTunnel = builder.AddDevTunnel("web-tunnel")
    .WithReference(web.GetEndpoint("https"), allowAnonymous: true);

builder.AddProject<Projects.Muster_Bot>("bot")
    .WithReference(db)
    .WithEnvironment("Discord__Token", discordToken)
    // Public tunnel URL → the quest-board embeds deep-link (title/author) and load their tier/guild icons from a
    // host Discord can actually reach.
    .WithEnvironment("Web__BaseUrl", webTunnel.GetEndpoint(web, "https"))
    .WaitForCompletion(migrations);

builder.Build().Run();
