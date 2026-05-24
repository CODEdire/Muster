# Deployment & CI/CD

Muster deploys to **Azure Container Apps** using **Bicep generated from the Aspire
AppHost** via `azd`, driven by an **Azure DevOps** pipeline connected to GitHub.

## Azure resources

| Resource | Purpose |
| --- | --- |
| Container Apps Environment | hosts the bot, web, and migration job |
| Azure SQL (server + database) | system of record; passwordless via Entra |
| Key Vault | Discord token, OAuth client secret |
| Azure Container Registry (ACR) | container images |
| Log Analytics + Application Insights | telemetry from `ServiceDefaults` |
| User-assigned managed identities | Container Apps → Key Vault + SQL; ACR pull |

### Container Apps scaling

- **muster-bot** — `min = max = 1` (stateful gateway singleton). Sharding/replicas post-v1.
- **muster-web** — scales `1..N` (stateless).
- **muster-migrations** — a run-once Job executed before bot/web revisions roll.

## Passwordless SQL (Entra)

Production uses **Entra managed identity** — no SQL password in any connection string:

1. Grant the Container Apps' managed identity `db_datareader` / `db_datawriter` (and DDL
   rights for the migration identity) on the database.
2. EF connects with `Authentication=Active Directory Default`.
3. Locally, a SQL Server container (via Aspire) is used instead.

## Infrastructure as code

Generate Bicep from the AppHost model:

```bash
azd init        # one-time, in repo root
azd infra synth # emit Bicep under ./infra from Muster.AppHost
```

Keep the generated `./infra` in source control and review changes on each app-model edit.

## Azure DevOps pipeline (outline)

`azure-pipelines.yml` stages:

1. **restore / build** — `dotnet build Muster.slnx`
2. **test** — `dotnet test` (unit + integration via Testcontainers)
3. **provision / deploy** — `azd provision` + `azd deploy` (builds & pushes images to ACR,
   runs the migration job, rolls bot/web revisions)

**Service connections:** GitHub (source) and Azure (ARM / workload identity federation).
**Environments:** `dev` → `staging` → `prod`, with approvals before prod.

## Promotion flow

```
GitHub PR ─► Azure DevOps build+test ─► deploy dev ─► deploy staging ─► (approval) ─► prod
```

Migrations always run as the gated job before new app revisions receive traffic.
