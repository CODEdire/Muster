# Deployment & CI/CD

Muster deploys to **Azure Container Apps** using **Bicep generated from the Aspire
AppHost** via `azd`, driven by an **Azure DevOps** pipeline connected to GitHub.

## First-deploy bootstrap checklist (per environment)

Some resources can't be fully provisioned by the AppHost — passwordless data-plane grants, schema the IaC
tooling doesn't model, and cross-RG/cross-tenant grants. These are **one-time per environment** (and stable
across redeploys). On a brand-new environment, after the first `aspire deploy`, work this list:

| # | Step | Why it's manual | Details |
|---|------|-----------------|---------|
| 1 | **SQL identity grants** — create the web/bot/migrations DB users + roles | Server bound `AsExisting`, so Aspire skips its auto-grant script | [Passwordless SQL](#passwordless-sql-entra) |
| 2 | **Wolverine message-store schema** — `db-apply` the `muster.wolverine_*` tables | Auto-provisioning removed (lock contention); managed out of band | [Wolverine message-store schema](#wolverine-message-store-schema) |
| 3 | **Data Protection wrap key** — `az keyvault key create muster-dp-wrap` | Neither Aspire nor Azure.Provisioning models a KV *key* resource | [Create the wrap key](#create-the-wrap-key-one-time-per-environment) |
| 4 | **Shared ACR pull grant** — `AcrPull` for the env MI on the shared registry | Registry is cross-RG; the grant can't be inlined in the env module | [Shared ACR pull grant](#shared-acr-pull-grant-entra) |
| 5 | **Custom domain cert** (optional) — bind the managed TLS cert | Azure issues the cert only after the hostname is live + DNS validates | [Custom domain + SSL](#custom-domain--ssl-for-the-web) |

After steps 1–4, restart the web + bot revisions so they pick up the grants/keys. Each step's section explains
the propagation/stale-principal gotchas (notably: if you redeploy the managed identities, data-plane grants made
against the old principal go stale — re-run the relevant grant).

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
  `AddAzureContainerRegistry(...).PublishAsExisting(name, rg)` (wired in
  [`ContainerRegistryExtensions`](../aspire/Muster.AppHost/PlatformExtensions/ContainerRegistryExtensions.cs)).
  Because the registry lives in a **different resource group** from the deploy, the environment's `AcrPull`
  grant **cannot** be emitted automatically: a `roleAssignment` can't be scoped to a cross-RG resource from
  the environment module (Bicep `BCP139`). `AddContainerEnvironment` therefore strips Aspire's inline grant
  when the registry is existing, and the `AcrPull` for the environment's managed identity is a **one-time
  out-of-band step** — see [Shared ACR pull grant](#shared-acr-pull-grant-entra) below. (For a per-env
  registry — `ContainerRegistryOptions:UseExisting=false` — the registry is in the deploy RG and the inline
  grant is kept, so no manual step is needed.)

User-secret parameters for the shared ACR (per AppHost environment):

```bash
cd aspire/Muster.AppHost
dotnet user-secrets set "Parameters:acrName" "<shared-acr-name>"
dotnet user-secrets set "Parameters:acrResourceGroup" "<shared-platform-rg>"
```

In Azure DevOps / GitHub Actions, set the same as pipeline variables (or use a single Key Vault per
environment that holds both the Muster-specific secrets and the platform ACR references).

## Custom domain + SSL for the web

Container Apps gives every app a free `*.<env>.<region>.azurecontainerapps.io` hostname with a managed
HTTPS cert out of the box. A custom domain (e.g. `app.musterbot.com`) is **wired in the AppHost** and driven
by [`WebCustomDomainOptions`](../aspire/Muster.AppHost/Options/WebCustomDomainOptions.cs) — no manual
`az containerapp hostname` steps and no out-of-band drift. [`WebHostingExtensions`](../aspire/Muster.AppHost/WebHostingExtensions.cs)
passes the hostname + managed-cert name to Aspire's `ConfigureCustomDomain(...)` helper, which emits the
ingress `customDomains` entry and (once a cert name is set) the managed-cert binding.

The hostname is config (`WebCustomDomainOptions:Domain`). The **certificate name is a prompted parameter**
(`webCustomDomainCertificateName`) — Azure issues the managed cert only **after** the domain's ownership +
CNAME validate, which can't happen until the hostname is already live on the app, so the cert name isn't known
at first-deploy time. Binding is therefore **two-phase**:

**Phase 1 — bind the hostname unbound.** Set the domain:

```jsonc
// appsettings.Production.json (or user-secrets / App Configuration)
"WebCustomDomainOptions": { "Domain": "app.musterbot.com" }
```

Deploy (`aspire deploy`). When prompted for **`webCustomDomainCertificateName`, leave it blank** and continue —
the hostname binds with `bindingType: 'Disabled'` (no TLS yet), which is what lets DNS validation proceed.

**Phase 2 — DNS, issue cert, bind TLS.**

1. **DNS** — at your registrar, create a CNAME `app.musterbot.com → <env-default-domain>`. For an apex
   (`musterbot.com`), use an `A` record to the env's static IP + a `TXT` record (`asuid.<domain>` carrying
   the app's verification id from `az containerapp show ... --query "properties.customDomainVerificationId"`).
2. **Issue the free managed certificate** (Azure-issued via ACME, auto-renewed) once DNS resolves:
   ```bash
   az containerapp env certificate create \
     --name <env-name> --resource-group <env-rg> \
     --hostname app.musterbot.com --validation-method CNAME
   ```
   (`--validation-method` is `CNAME` for subdomains, `TXT`/`HTTP` for apex.) Note the **certificate name** it
   creates (`az containerapp env certificate list -n <env-name> -g <env-rg> -o table`).
3. **Bind TLS** — `aspire deploy` again and **enter the cert name at the `webCustomDomainCertificateName`
   prompt**. That flips `bindingType` to `SniEnabled` and attaches the cert; subsequent deploys preserve it.
   For CI / non-interactive deploys, supply the name via config instead of the prompt:
   ```bash
   dotnet user-secrets set "Parameters:webCustomDomainCertificateName" "<managed-cert-name>"
   # or set the same as a pipeline variable / App Configuration key
   ```

**Optional — front with Azure Front Door / Application Gateway** for WAF, multi-region, or anycast. The
Container App stays the origin; Front Door terminates TLS at the edge (and you can skip the per-app managed
cert entirely, letting Front Door own the public cert).

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
dotnet user-secrets set "Parameters:sqlServerName" "your-sql-server-resource-name"
dotnet user-secrets set "Parameters:sqlResourceGroup" "your-sql-resource-group"
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

**4. Run the grant script** — [`aspire/Muster.AppHost/sql/grant-managed-identities.sql`](../aspire/Muster.AppHost/sql/grant-managed-identities.sql).
Open it, paste the three managed-identity names from step 2 into the `@identities` list at the top, and run it
against the **application database** (not `master`). It creates each contained user `FROM EXTERNAL PROVIDER`,
grants `CONNECT`, and adds it to `db_owner`. The script is **idempotent** (guards `CREATE USER` with a
`sys.database_principals` check and the role add with `IS_ROLEMEMBER`), so it's safe to re-run and safe to
drop into a CI bootstrap step.

All three identities get `db_owner` (full DDL + DML) for simplicity — the migration job needs DDL, and web/bot
run under the same role. For least privilege instead, give web/bot `db_datareader` + `db_datawriter` and keep
only migrations at `db_owner` (or `db_ddladmin` + targeted GRANTs); edit the role line in the script per-identity.

**5. Re-deploy / restart Container Apps**. They pick up the SQL grant on the next connection — no app
config change needed.

### Local development

Local `dotnet run` uses a SQL Server container (Aspire `RunAsContainer` — see PersistenceExtensions).
No Entra setup; the container handles its own auth and the Aspire-generated connection string carries
everything.

## Shared ACR pull grant (Entra)

The Container Apps Environment pulls images with its **own user-assigned managed identity** (`muster_env_mi`),
created by `WithAzureContainerRegistry(...)`. When the registry is the shared, cross-RG ACR, that identity's
`AcrPull` role can't be authored by the deploy (cross-RG `roleAssignment` → Bicep `BCP139`), so it's a manual
one-shot per environment — the registry equivalent of [Passwordless SQL](#passwordless-sql-entra).

### Why this is a manual one-shot for us

`AddContainerEnvironment` strips Aspire's inline `AcrPull` grant whenever `ContainerRegistryOptions:UseExisting`
is `true` (see [`ContainerRegistryExtensions`](../aspire/Muster.AppHost/PlatformExtensions/ContainerRegistryExtensions.cs)).
The environment identity, the `registries` block, and image-pull config are all still emitted — only the grant
is deferred. The identity's name is `muster_env_mi-<uniqueString(rg.id)>`, which is **stable across redeploys**
in the same resource group, so the grant is done once and persists.

### Setup runbook (per environment)

**1. Deploy once** so the environment identity exists (the apps will report `ImagePullBackOff` until step 3 —
expected on the very first deploy):

```bash
aspire deploy
```

**2. Grant the environment identity `AcrPull` on the shared registry** (run by someone with `Owner` /
`User Access Administrator` on the shared platform RG):

```bash
# Environment MI principalId (from the deploy RG)
ENV_MI_PRINCIPAL=$(az identity list -g <env-rg> \
  --query "[?starts_with(name,'muster_env_mi')].principalId | [0]" -o tsv)

# Shared registry resource id (from the shared platform RG)
ACR_ID=$(az acr show -n <shared-acr-name> -g <shared-platform-rg> --query id -o tsv)

az role assignment create \
  --assignee-object-id "$ENV_MI_PRINCIPAL" \
  --assignee-principal-type ServicePrincipal \
  --role AcrPull \
  --scope "$ACR_ID"
```

**3. Restart the container apps** (or re-run `aspire deploy`) so the new revisions pull successfully:

```bash
az containerapp revision restart -n muster-web        -g <env-rg> --revision <latest>
az containerapp revision restart -n muster-bot        -g <env-rg> --revision <latest>
# migrations is a Job — it pulls on the next execution
```

**Idempotent re-runs**: `az role assignment create` is a no-op if the assignment already exists (it returns the
existing one), so this is safe to fold into a CI bootstrap step.

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

## Wolverine message-store schema

There are **two** schemas in the application database, managed by **two different** mechanisms:

| Schema | Owns | Managed by |
|--------|------|------------|
| `dbo.*` (app tables) | domain data | EF Core migrations — applied by the migration job's default run (`Database.MigrateAsync`) |
| `muster.wolverine_*` (durable inbox/outbox) | Wolverine message store | **exported SQL scripts / the JasperFx `db-*` CLI — applied out of band** (this section) |

### Why no auto-provisioning

Wolverine can build its store schema on startup (`AutoBuildMessageStorageOnStartup`), but we set it to
**`AutoCreate.None` on every host** (`WolverineExtensions.AddMusterMessaging`). On a fresh database, multiple
replicas (web + bot) auto-building the same `muster.*` objects concurrently — and racing EF's migration — block
on schema-modification (`Sch-M`) locks and **hang the deploy** (`SqlException` error `-2`, "Execution Timeout
Expired"). Explicit, single-threaded, reviewed schema changes avoid that entirely. This follows Wolverine's own
guidance: [Managing the message store → Exporting SQL scripts](https://wolverinefx.net/guide/durability/managing.html#exporting-sql-scripts).

### The `db-*` CLI (exposed by the migration host)

`Muster.MigrationService` registers Wolverine and routes any `db-*` argument to JasperFx
(`host.RunJasperFxCommands(args)`), so it doubles as the schema tool:

```bash
cd src/Muster.MigrationService

# Export the message-store DDL to a script (review-able, commit-able)
dotnet run -- db-dump ../../aspire/Muster.AppHost/sql/wolverine-store.sql

# Show what would change against the current target database (uses ConnectionStrings:musterdb)
dotnet run -- db-assert        # exits non-zero if the DB is out of sync — good CI gate
dotnet run -- db-patch         # write a patch script for the outstanding delta only

# Apply outstanding changes directly to the target database
dotnet run -- db-apply
```

> The connection used is `ConnectionStrings:musterdb` from the host's config — for a non-local target set it
> (user-secrets / env / `Parameters:*`) before running, and ensure your identity has DDL rights on that DB.

### Production workflow (per environment, on first deploy + on Wolverine version bumps)

1. **Export & review** — run `db-dump` against a dev DB, commit the script. Wolverine's store schema only
   changes when the Wolverine package itself changes, so this is rare.
2. **Apply once** — with DDL rights on the target DB, either run the committed script directly
   (`sqlcmd -G -d MusterBot -i wolverine-store.sql`) **or** run `db-apply` (it diffs + patches, idempotent).
   Do this **once per environment**, with nothing else provisioning — single-threaded, no `Sch-M` contention.
3. **Verify** — `db-assert` post-deploy (optionally as a CI gate) confirms the live DB matches the model.

Because no app auto-provisions, web/bot assume the `muster.wolverine_*` tables already exist — apply step 2
**before** they start (it's the message-store analogue of the [Passwordless SQL](#passwordless-sql-entra) and
[Shared ACR pull grant](#shared-acr-pull-grant-entra) one-time steps).

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

#### Create the wrap key (one-time per environment)

The AppHost provisions the Key Vault and grants web/bot **Key Vault Crypto User** (wrap/unwrap), but it does
**not** create the `muster-dp-wrap` key itself — neither Aspire nor Azure.Provisioning models a Key Vault *key*
resource (only secrets), and Crypto User cannot create keys. So the key is a manual bootstrap step, like the
[passwordless SQL grant](#passwordless-sql-entra). Without it, web/bot fail at startup trying to wrap the key
ring against a key that doesn't exist.

```bash
# Name MUST be muster-dp-wrap (matches DataProtection:WrapKeyName, set by the AppHost).
az keyvault key create \
  --vault-name <kv-name> \
  --name muster-dp-wrap \
  --kty RSA --size 2048 \
  --ops wrapKey unwrapKey
```

The operator running this needs **Key Vault Crypto Officer** on the vault (RBAC vaults — Crypto *User* can
wrap/unwrap but not create). Grant it temporarily if needed:
```bash
az role assignment create --assignee <you> --role "Key Vault Crypto Officer" --scope <kv-resource-id>
```
Then restart the web + bot revisions so they pick up the now-existing key.

**No migration** from the previous SQL-backed key ring — we cut over fresh for v0.5. Existing
connector secrets in lower environments are re-entered after the cutover. (See `docs/persistence.md`
"v1 fresh-schema baseline" — DP keys are part of that cutover.)

**Expected after any key-ring cutover**: clients holding a cookie protected by a now-absent key log
`CryptographicException: The key {…} was not found in the key ring` (e.g. antiforgery). This is **non-fatal** —
the antiforgery path (`GetCookieTokenDoesNotThrow`) swallows it and re-issues a token against the current ring,
so it self-heals on the next request (a cookie clear fixes it immediately). It should only affect cookies minted
*before* the cutover. If **new** sessions throw it, the ring isn't persisting — verify `keys.xml` exists in the
`dpkeys` container and that all web replicas share it (blob + KV wrap), rather than each minting an ephemeral key.

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
