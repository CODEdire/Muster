# Muster Documentation

Muster is a multi-tenant Discord participation-tracking bot with a Blazor SSR web UI.
It records participation through **tracking sessions** (voice presence during ops), a
two-type **missions board** (quests + event ops), **reaction musters**, and **manual
awards**, recording rewards against a **multi-currency ledger** (seasonal Points +
persistent spendable currencies like Coin).

## Index

| Doc | What's inside |
| --- | --- |
| [architecture.md](./architecture.md) | Services, topology, scaling/sharding strategy |
| [data-model.md](./data-model.md) | Entities, relationships, ledger/seasons/wallets |
| [messaging.md](./messaging.md) | Wolverine CQRS, contracts, durable outbox, sagas |
| [discord-integration.md](./discord-integration.md) | NetCord intents, install, commands, events |
| [web-and-auth.md](./web-and-auth.md) | Blazor SSR, Discord OAuth, authorization model |
| [api.md](./api.md) | Public API + external connector / "Coin" contract |
| [deployment.md](./deployment.md) | Aspire → azd → Azure Container Apps, CI/CD |
| [local-dev.md](./local-dev.md) | Prerequisites, secrets, running locally |
| [operations.md](./operations.md) | Observability, retention, privacy/ToS, runbooks |

## Tech stack

- **.NET 10** (LTS), **.NET Aspire** orchestration
- **NetCord** for the Discord gateway and interactions
- **EF Core** against **Azure SQL** (passwordless via Entra managed identity in prod)
- **Wolverine** for CQRS, durable outbox/inbox, sagas
- **Blazor SSR** (static server rendering — no interactive/SignalR mode)
- **Azure Container Apps**, deployed via **azd**-generated Bicep and **Azure DevOps**

## Solution layout

```
src/
  Muster.AppHost           Aspire orchestrator
  Muster.ServiceDefaults   OTel / health / resilience shared defaults
  Muster.Domain            Entities + enums (no infra dependencies)
  Muster.Contracts         Wolverine message contracts (broker-agnostic)
  Muster.Infrastructure    EF Core DbContext, migrations, Wolverine setup
  Muster.Bot               NetCord gateway worker
  Muster.Web               Blazor SSR UI + Wolverine.HTTP API
  Muster.MigrationService  Run-once EF migration job
tests/
  Muster.UnitTests
  Muster.IntegrationTests
```

See [`../Features.md`](../Features.md) for the implementation checklist.
