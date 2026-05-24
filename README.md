# Muster

Muster is a multi-tenant **Discord participation-tracking bot** with a Blazor SSR web UI.
It records member participation through:

- **Tracking sessions** — bounded windows (manual or bound to a Discord Scheduled Event)
  where **voice presence** and **reaction check-ins** are rewardable.
- **Missions** — a two-type board: claimable **quests** (submit → officer approve) and
  scheduled **event ops** (RSVP / attendance).
- **Reaction musters** — one-off react-to-check-in messages.
- **Manual / bulk awards** — for off-platform contributions.

Rewards are recorded against a **multi-currency ledger**: seasonal **Points** drive
leaderboards, while persistent spendable currencies (e.g. **Coin**) are exposed to external
loot/economy connectors via an API.

## Tech stack

.NET 10 · .NET Aspire · NetCord · EF Core / Azure SQL · Wolverine (CQRS + durable outbox) ·
Blazor SSR · Azure Container Apps · deployed via `azd` + Azure DevOps.

## Getting started

```bash
dotnet build Muster.slnx
dotnet test  Muster.slnx
dotnet run --project src/Muster.AppHost   # requires Docker + a Discord token
```

See [docs/local-dev.md](docs/local-dev.md) for setup and secrets.

## Documentation

Full design docs live in [`docs/`](docs/README.md). The implementation roadmap and progress
are tracked in [`Features.md`](Features.md).

## License

See [LICENSE](LICENSE).
