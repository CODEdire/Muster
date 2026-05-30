# Local Development

## Prerequisites

- **.NET 10 SDK** (10.0.1xx)
- **Docker** (Aspire runs a SQL Server container locally)
- A **Discord application** for testing — create one at the Discord Developer Portal to get
  a bot **token** and OAuth **client id/secret**.
- EF tooling for migrations: `dotnet tool install --global dotnet-ef --version 10.0.*`

## First build

```bash
dotnet restore Muster.slnx
dotnet build   Muster.slnx
dotnet test    Muster.slnx
```

## Secrets

The AppHost reads Discord credentials as parameters. Set them via user-secrets on the
AppHost project (never commit secrets):

```bash
cd aspire/Muster.AppHost
dotnet user-secrets set "Parameters:discordToken"         "<bot-token>"
dotnet user-secrets set "Parameters:discordClientId"      "<oauth-client-id>"
dotnet user-secrets set "Parameters:discordClientSecret"  "<oauth-client-secret>"
```

These flow to the bot (`Discord__Token`) and web (`Discord__ClientId` /
`Discord__ClientSecret`) as environment variables.

## Run everything (Aspire)

```bash
dotnet run --project aspire/Muster.AppHost
```

This launches the **Aspire dashboard**, starts a SQL Server container, runs the migration
job, then starts the bot and web. The dashboard links to the web app and shows logs,
traces, and health for every resource.

> Without Docker, the SQL container won't start; without a Discord token the bot won't
> connect. The solution still **builds** and the web app still starts — useful for UI work.

## Working with the database

```bash
# add a migration after changing entities / DbContext
dotnet ef migrations add <Name> --project src/Muster.Infrastructure

# the design-time factory (MusterDbContextFactory) means no running DB is needed to scaffold
```

Migrations are applied at runtime by `Muster.MigrationService` (and on `azd` deploy), not
by the bot or web.

## Project quick map

| Run target | Command |
| --- | --- |
| Everything (orchestrated) | `dotnet run --project aspire/Muster.AppHost` |
| Web only | `dotnet run --project src/Muster.Web` |
| Bot only | `dotnet run --project src/Muster.Bot` |
| Migrations only | `dotnet run --project src/Muster.MigrationService` |
