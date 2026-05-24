var builder = DistributedApplication.CreateBuilder(args);

// Azure SQL in publish; a SQL Server container locally so `dotnet run` works without Azure.
var sql = builder.AddAzureSqlServer("sql")
    .RunAsContainer();

var db = sql.AddDatabase("musterdb");

// Discord credentials are AppHost parameters -> user-secrets locally, Key Vault refs in Azure.
var discordToken = builder.AddParameter("discord-token", secret: true);
var discordClientId = builder.AddParameter("discord-clientid", secret: true);
var discordClientSecret = builder.AddParameter("discord-clientsecret", secret: true);

// Run-once migration job applies the schema before the bot/web start.
var migrations = builder.AddProject<Projects.Muster_MigrationService>("migrations")
    .WithReference(db)
    .WaitFor(db);

builder.AddProject<Projects.Muster_Bot>("bot")
    .WithReference(db)
    .WithEnvironment("Discord__Token", discordToken)
    .WaitForCompletion(migrations);

builder.AddProject<Projects.Muster_Web>("web")
    .WithReference(db)
    .WithEnvironment("Discord__ClientId", discordClientId)
    .WithEnvironment("Discord__ClientSecret", discordClientSecret)
    .WithExternalHttpEndpoints()
    .WaitForCompletion(migrations);

builder.Build().Run();
