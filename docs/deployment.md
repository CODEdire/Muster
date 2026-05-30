# Deployment & CI/CD

Muster deploys to **Azure Container Apps** using **Bicep generated from the Aspire
AppHost** via `azd`, driven by an **Azure DevOps** pipeline connected to GitHub.

## Azure resources

| Resource | Purpose |
| --- | --- |
| Container Apps Environment | hosts the bot, web, and migration job |
| Azure SQL (server + database) | system of record; passwordless via Entra |
| Key Vault | secrets: Discord token, OAuth client secret, Azure SignalR conn string, DP wrap key |
| App Configuration | non-secret dynamic config (feature flags, role/channel ids, per-env knobs) |
| Storage account (Blob) | Data Protection key ring (`dpkeys` container) |
| Azure Container Registry (ACR) | container images |
| Application Insights (+ Log Analytics workspace) | OTEL traces/metrics/logs from `ServiceDefaults` via Azure Monitor exporter |
| User-assigned managed identities | Container Apps → KV + App Config + Storage + SQL; ACR pull |

### Container Apps scaling

Sizing + scale rules are encoded in code, not folklore — see
[`BotHostingExtensions`](../aspire/Muster.AppHost/BotHostingExtensions.cs),
[`WebHostingExtensions`](../aspire/Muster.AppHost/WebHostingExtensions.cs), and
[`MigrationsExtensions`](../aspire/Muster.AppHost/MigrationsExtensions.cs).

| App | Replicas | vCPU | Memory | Grace | Notes |
| --- | --- | --- | --- | --- | --- |
| **muster-bot** | `min = max = 1` | 0.25 | 0.5 GiB | 60s | Gateway singleton — Discord rejects a second concurrent session per token. Sharding post-v1. |
| **muster-web** | `min = 1`, `max = 5` | 0.5 | 1.0 GiB | 30s | One always-warm replica so Blazor circuits don't drop to zero; scales on traffic. |
| **muster-migrations** | Container App Job (manual trigger) | 1.0 | 2.0 GiB | — | Runs once per deploy via pipeline `az containerapp job start`. |

## Hosting model: dedicated env, shared registry

- **Container Apps Environment** — one **dedicated environment per env** (`muster-dev`, `muster-staging`,
  `muster-prod`). Each gets its own Log Analytics workspace + VNet posture + workload-profile config.
  Sharing a CA Environment across unrelated products is a co-tenancy choice we explicitly avoid; the
  per-env runtime boundary matters more than the small overhead of one extra environment.
- **Azure Container Registry (ACR)** — **shared registry in a shared "platform" resource group**.
  Image storage is naturally cross-product; each Container Apps Environment binds to it via Aspire's
  `AddAzureContainerRegistry(...).AsExisting(...)` (wired in
  [`ContainerRegistryExtensions`](../aspire/Muster.AppHost/ContainerRegistryExtensions.cs)). Aspire
  emits the `AcrPull` role assignment for each environment's identity automatically.

User-secret parameters for the shared ACR (per AppHost environment):

```bash
cd aspire/Muster.AppHost
dotnet user-secrets set "Parameters:acr-name" "<shared-acr-name>"
dotnet user-secrets set "Parameters:acr-resource-group" "<shared-platform-rg>"
```

In Azure DevOps / GitHub Actions, set the same as pipeline variables (or use a single Key Vault per
environment that holds both the Muster-specific secrets and the platform ACR references).

## Custom domain + SSL for the web

Container Apps gives every app a free `*.<env>.<region>.azurecontainerapps.io` hostname with a managed
HTTPS cert out of the box. For a custom domain (e.g. `app.musterbot.com`):

1. **DNS** — at your registrar, create a CNAME `app.musterbot.com → <env-default-domain>`. For an apex
   (`musterbot.com`), use an `A` record to the env's static IP + a `TXT` record (`asuid.<domain>` carrying
   the app's verification id from `az containerapp show ... --query "properties.customDomainVerificationId"`).
2. **Verify + add the hostname**:
   ```bash
   az containerapp hostname add \
     --hostname app.musterbot.com \
     --name muster-web --resource-group <env-rg>
   ```
3. **Bind a managed cert** (free, Azure-issued via ACME, auto-renewed):
   ```bash
   az containerapp hostname bind \
     --hostname app.musterbot.com \
     --environment <env-name> \
     --name muster-web --resource-group <env-rg> \
     --validation-method CNAME
   ```
   (`--validation-method` is `CNAME` for subdomains, `TXT` or `HTTP` for apex — depends on your DNS shape.)
4. **Optional — front with Azure Front Door / Application Gateway** for WAF, multi-region, or anycast.
   The Container App stays the origin; Front Door terminates TLS at the edge.

Aspire's `PublishAsAzureContainerApp(...)` callback can declare the hostname in `app.Configuration.Ingress.CustomDomains`,
but managed-cert binding requires the DNS verification step to be live first — so the standard pattern
is: declare the cert/hostname desired state out-of-band (Bicep or the az commands above), do the bind
**once per environment** as a manual pipeline step.

## Passwordless SQL (Entra)

Production uses **Entra managed identity** — no SQL password in any connection string. Microsoft.Data.SqlClient
acquires an access token via the Container App's user-assigned managed identity at request time.

### Why this is a manual one-shot for us

We bind to a pre-existing Azure SQL Server via `AsExisting()` in
[`aspire/Muster.AppHost/PersistenceExtensions.cs`](../aspire/Muster.AppHost/PersistenceExtensions.cs). When
Aspire fully provisions a new server it also emits a Bicep `deploymentScripts` resource that runs the
T-SQL `CREATE USER` / `ALTER ROLE` for you — but that script is **explicitly skipped** when the server is
existing. So the SQL user mapping is a manual step per environment, done once.

### Setup runbook (per environment)

**0. Set the AppHost parameters** so the AppHost binds to the right server:

```powershell
cd aspire/Muster.AppHost
dotnet user-secrets set "Parameters:sql-server-name" "your-sql-server-resource-name"
dotnet user-secrets set "Parameters:sql-resource-group" "your-sql-resource-group"
```
(In CI/CD, set these as pipeline variables or via Key Vault references.)

**1. Deploy once** so azd creates the Container Apps + their user-assigned managed identities:

```bash
azd up        # first time
# or
azd deploy    # subsequent runs
```

**2. Find each Container App's managed identity name.** azd creates one user-assigned MI per project:

```bash
# list every MI in the resource group
az identity list -g <env-rg> --query "[].{Name:name, PrincipalId:principalId}" -o table

# or read it straight off the container app
az containerapp show -n <app-name> -g <env-rg> --query "identity.userAssignedIdentities" -o json
```

You'll see three identities (bot, web, migrations). Note the **Name** of each.

**3. Connect to the database as an Entra admin** (you, or whoever owns the SQL server's Entra admin role).
Easiest is Azure Portal → SQL Database → Query editor, signing in with your Entra account.

**4. Run this T-SQL once per identity**:

```sql
-- Web app: full read/write
CREATE USER [<web-mi-name>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader  ADD MEMBER [<web-mi-name>];
ALTER ROLE db_datawriter  ADD MEMBER [<web-mi-name>];

-- Bot: same as web
CREATE USER [<bot-mi-name>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader  ADD MEMBER [<bot-mi-name>];
ALTER ROLE db_datawriter  ADD MEMBER [<bot-mi-name>];

-- Migration job: needs DDL rights
CREATE USER [<migrations-mi-name>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_owner       ADD MEMBER [<migrations-mi-name>];
```

`db_owner` on the migration MI is the simplest path; tighten to `db_ddladmin` + targeted GRANTs if you
want least-privilege.

**5. Re-deploy / restart Container Apps**. They pick up the SQL grant on the next connection — no app
config change needed.

**Idempotent re-runs**: `CREATE USER` fails if the user already exists; wrap in
`IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '<mi-name>')` if you script it for CI.

### Local development

Local `dotnet run` uses a SQL Server container (Aspire `RunAsContainer` — see PersistenceExtensions).
No Entra setup; the container handles its own auth and the Aspire-generated connection string carries
everything.

## Migration job orchestration

The `Muster.MigrationService` is a run-once schema bootstrap (EF Core `Database.MigrateAsync`).
[`aspire/Muster.AppHost/MigrationsExtensions.cs`](../aspire/Muster.AppHost/MigrationsExtensions.cs)
shapes it per environment:

| Mode | Shape | Gate |
| --- | --- | --- |
| Local `dotnet run` | Normal Aspire project | `WaitForCompletion(migrations)` in `AppHost.cs` blocks bot + web until it exits |
| Publish (azd) | **Container App Job** (`Microsoft.App/jobs`, manual trigger) | Pipeline runs the job + waits before rolling new bot/web revisions |

### Why a Container App Job and not a plain Container App

A long-running Container App auto-restarts after exit. The MigrationService is a one-shot —
restarting it would re-run the migration loop forever. Jobs are the correct primitive:
run-to-completion, exit code surfaced, no replicas.

We attach Aspire's `AzureContainerAppJobCustomizationAnnotation` (marked experimental as
`ASPIREAZURE002` in 13.3.5) which flips the Bicep generator from `Microsoft.App/containerApps`
to `Microsoft.App/jobs`. When the stable `PublishAsAzureContainerAppJob` extension lands, the
file collapses to a single call.

### Pipeline contract (deploy-time)

`azd` deploys the job alongside the apps, but does NOT start it for you — you start it and wait
explicitly in the pipeline:

```bash
# 1. Deploy resources (jobs + apps). azd respects the existing revisions on apps; new revisions
#    are not promoted yet because we're going to roll them in step 3.
azd deploy

# 2. Start the migration job, wait for completion, fail the pipeline if it errors.
az containerapp job start \
  --name <migrations-job-name> \
  --resource-group <env-rg>

# Poll for completion (or use `az containerapp job execution show --query "properties.status"`):
az containerapp job execution list \
  --name <migrations-job-name> \
  --resource-group <env-rg> \
  --query "[0].properties.{Status:status,StartTime:startTime,EndTime:endTime}" -o table

# 3. Once migration succeeded, roll new bot + web revisions to production traffic.
az containerapp revision activate \
  --name muster-web --resource-group <env-rg> \
  --revision <new-revision>

az containerapp revision activate \
  --name muster-bot --resource-group <env-rg> \
  --revision <new-revision>
```

The migration job's container image is built and pushed by `azd deploy` just like any other Container
App; only the runtime shape (job vs app) differs.

### What happens if migrations fail in production

The job exits non-zero → step 2 above fails → pipeline aborts before step 3. The previous
bot/web revisions stay serving traffic — no half-deployed state. Re-run the pipeline after fixing
the migration (the bootstrap is idempotent: `Database.MigrateAsync` is a no-op when up-to-date).

## Telemetry: Aspire Dashboard (run) vs App Insights (publish)

| Mode | Sink | Wiring |
| --- | --- | --- |
| Run (`dotnet run`) | **Aspire Dashboard** (in-process) | OTLP exporter — Aspire injects `OTEL_EXPORTER_OTLP_ENDPOINT` into every project at run time |
| Publish (azd) | **Azure Application Insights** (+ Log Analytics workspace) | `UseAzureMonitor()` reads `APPLICATIONINSIGHTS_CONNECTION_STRING` published by `WithReference(appInsights)` |

The Aspire Dashboard does **not** deploy to Azure. In production you observe via:
- **Application Insights** — OTEL traces, metrics, logs from the AspNetCore + HttpClient + Runtime
  instrumentations and any custom `ActivitySource`s (e.g. Wolverine middleware spans)
- **Container Apps portal** — per-revision live logs, replica count, scaling decisions
- **Log Analytics workspace** — raw container stdout/stderr + KQL across all envs

App Insights is provisioned fresh per env by `ApplicationInsightsExtensions.AddMusterApplicationInsights()`
in the AppHost — null in run mode. The existing OTEL pipeline in `ServiceDefaults.ConfigureOpenTelemetry`
rides both exporters: OTLP fires when the env var is present (local), Azure Monitor fires when its
connection string is present (publish). Same instrumentation list in both modes.

What gets tracked + per-surface span conventions (bot commands, interactions, autocomplete, background
services, Wolverine handlers) is documented in [`observability.md`](observability.md).

## Configuration sources (KV + App Configuration)

Per-environment splits between Key Vault (secrets) and App Configuration (non-secret dynamic config).
Both are provisioned fresh per environment by Aspire — no `AsExisting` binding.

| Concern | Lives in | Read by |
| --- | --- | --- |
| Discord bot token, OAuth client secret, SignalR conn string | **Key Vault** | Web + Bot (via the Aspire KV-as-config source) |
| Feature flags, role/channel ids, per-env knobs | **App Configuration** | Web + Bot (via the Aspire App Config-as-config source) |
| DP wrap RSA key (`muster-dp-wrap`) | **Key Vault** | Data Protection (`ProtectKeysWithAzureKeyVault`) |
| DP key ring (`keys.xml`) | **Storage / `dpkeys` blob container** | Data Protection (`PersistKeysToAzureBlobStorage`) |

**Wiring**: `KeyVaultExtensions` / `AppConfigurationExtensions` / `StorageExtensions` in the AppHost.
Both KV + App Config are publish-only (return `null` in run mode); Storage uses Azurite locally.

**RBAC** is handled by Aspire — `WithReference(kv)` / `WithReference(appconfig)` / `WithReference(dpKeys)`
in `WebHostingExtensions` + `BotHostingExtensions` grant the workload identity the right role
(`Key Vault Secrets User`, `Key Vault Crypto User`, `App Configuration Data Reader`,
`Storage Blob Data Contributor`).

**Client integration** in `Web/Program.cs` and `Bot/Program.cs`:

```csharp
if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("kv")))
{
    builder.Configuration.AddAzureKeyVaultSecrets("kv");
}
if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("appconfig")))
{
    builder.Configuration.AddAzureAppConfiguration("appconfig");
}
```

Both gate on connection-string presence so local dev (where AppHost's KV/AC extensions return null)
keeps reading from `appsettings.Development.json` + `dotnet user-secrets`.

### Data Protection setup

The Data Protection key ring (used by `IConnectorSecretProtector` to encrypt outbound webhook + currency
connector secrets at rest in SQL) splits storage from at-rest protection:

- **Storage** — Blob container `dpkeys` on the env's Storage account. The key ring file is `keys.xml`
  at the container root.
- **At-rest wrap** — RSA key `muster-dp-wrap` in the env's Key Vault. Each ring entry's symmetric key
  is wrapped by this asymmetric key on write, unwrapped on read.

`Muster.Infrastructure.AddMusterConnectorProtection` branches on config: if both URIs + the wrap key
name + container name are present (publish mode), it uses the Azure path; otherwise it falls back to
the EF-backed `PersistKeysToDbContext<MusterDbContext>()` so local dev stays SQL-only.

**No migration** from the previous SQL-backed key ring — we cut over fresh for v0.5. Existing
connector secrets in lower environments are re-entered after the cutover. (See `docs/persistence.md`
"v1 fresh-schema baseline" — DP keys are part of that cutover.)

**Cost** — KV operations for DP are tiny: a wrap on each new ring entry (default rotation is every
90 days) + an unwrap per host startup. Single-digit dollars-per-year per environment. Storage cost
is rounding error (the ring file is a few KB).

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

## CORS (Azure Container Apps ingress)

The `muster-web` app is **default-deny for cross-origin** — no `AddCors` / `UseCors` is registered in
[`Program.cs`](../src/Muster.Web/Program.cs). Cross-origin policy lives at the ACA ingress layer so the
in-app surface stays minimal and ops can adjust without redeploying code.

When a real browser-side API client lands (today there are none — the public API exists for server-side
connectors), configure the ingress CORS policy on the `muster-web` Container App:

```bash
az containerapp ingress cors enable \
  -n muster-web -g <env-rg> \
  --allowed-origins "https://your-client.example.com" \
  --allowed-methods GET POST PUT DELETE OPTIONS \
  --allowed-headers "X-Api-Key,Content-Type" \
  --max-age 600
```

Notes:

- Keep the allow-list tight. Avoid `*` for origin once any key with `write:*` scope exists.
- `Authorization` is not in the allowed-headers above because the public API uses `X-Api-Key`, not bearer
  tokens. Add it only if a future endpoint needs it.
- The cookie-auth web UI (`/guilds/...`, `/account/login`) is same-origin only; no CORS exposure needed for it.
- If we ever want stricter per-route CORS (e.g. `read:leaderboard` open, writes locked down), move the policy
  into `Program.cs` with `AddCors`/`UseCors` + per-endpoint `RequireCors(...)`. ACA can't do per-route policy.
