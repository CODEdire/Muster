# Observability

Muster instruments three layers: **Aspire's OTEL defaults** (HTTP, runtime, Wolverine), **custom bot
surfaces** (NetCord has no OTEL hooks, so we add them), and **Container Apps platform metrics** (CPU,
memory, replica count — emitted by Azure independently of the app).

In run mode telemetry feeds the **Aspire Dashboard** via OTLP. In publish mode the same OTEL pipeline
exports to **Azure Application Insights** via `UseAzureMonitor()`. See
[`deployment.md`](deployment.md) "Telemetry: Aspire Dashboard (run) vs App Insights (publish)" for the
config plumbing.

## Pipelines

| Pipeline | Source | Exporter | Where it lands |
| --- | --- | --- | --- |
| `OTEL_EXPORTER_OTLP_ENDPOINT` (Aspire injects) | every OTEL source | OTLP | Aspire Dashboard (run only) |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` (Aspire injects in publish) | every OTEL source | Azure Monitor | App Insights component (publish only) |
| Container Apps platform | Azure runtime | Azure Monitor (native) | Container App → Metrics blade; Log Analytics workspace |

The first two share **the same instrumentation list** in `ServiceDefaults.ConfigureOpenTelemetry` — no
separate config for prod vs local.

## Auto-instrumented surfaces

From `ServiceDefaults`:

- **AspNetCore** — incoming HTTP request spans (server kind). Web only — bot has no HTTP server.
- **HttpClient** — outgoing HTTP spans (client kind). Discord REST, currency connector calls.
- **Runtime** — GC, threadpool, CLR metrics.
- **Wolverine** — `ActivitySource("Wolverine")` + `Meter("Wolverine")`. Handler invocations,
  message sends/receives. Registered in ServiceDefaults so both hosts get it.

## Custom bot surfaces

NetCord ships no OTEL hooks. `Muster.Bot.Platform.Telemetry.BotTelemetry` adds them. Single
`ActivitySource("Muster.Bot")` + `Meter("Muster.Bot")`; both auto-registered by ServiceDefaults via
`builder.Environment.ApplicationName`.

| Surface | Where wired | Span name | Kind | Renders in App Insights as |
| --- | --- | --- | --- | --- |
| Slash command | `MusterModuleBase.RunAsync` | `slash <command>` | Server | Request (Performance blade row) |
| Quest interaction (button/select/modal) | `QuestInteractionDispatch.RunAsync` | `interaction quest:<CommandType>` | Server | Request |
| Background tick | each scheduler's try-block | `bg <SchedulerName>` | Server | Request |
| Wolverine handler | Wolverine built-in | `<MessageType>` | Consumer/Internal | Dependency |
| Outgoing Discord REST | HttpClient instrumentation | `POST /api/...` | Client | Dependency |
| Outgoing SQL | EF Core (if instrumentation added) | `SELECT/INSERT/...` | Client | Dependency |

Autocomplete intentionally does NOT span — Discord cancels after ~3s and we don't want span overhead in
the latency budget. Instead, `BotTelemetry.MeasureAutocomplete(provider)` records a counter
+ histogram (`muster.bot.autocomplete.count` / `.duration`).

### Naming convention

| Pattern | Example |
| --- | --- |
| `slash <command>` | `slash quest assign` |
| `interaction <feature>:<action>` | `interaction quest:ClaimQuest` |
| `bg <ServiceName>` | `bg LedgerPruneScheduler` |

Matches HTTP's `GET /<route>` shape so the Performance blade reads naturally.

### Tags on spans

Standard tags (some only set when present):

- `muster.surface` = `slash` / `interaction` / `background`
- `command.name` (slash) / `interaction.action` (interaction)
- `discord.guild.id`, `discord.user.id`, `discord.channel.id`
- `result` = `ok` / `error`
- `error.type` = exception type FullName (on failure)

### Tags on metrics

**Discipline**: only low-cardinality dims on metrics. Each unique tag-value combo = a separate time
series — `discord.user.id` would explode storage.

| Dimension | On spans | On metrics |
| --- | --- | --- |
| `command.name`, `result` | yes | yes |
| `provider` (autocomplete) | yes | yes |
| `discord.guild.id` | yes | yes (capped <500) |
| `discord.user.id` | yes | **no** |
| `discord.channel.id` | yes | **no** |

## Background services

Every `BackgroundService` in the bot wraps its periodic-tick try-block:

```csharp
using var activity = BotTelemetry.StartBackgroundTick(nameof(LedgerPruneScheduler));
try
{
    // work
    activity.SetResult(ok: true);
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    activity.SetException(ex);
    logger.LogError(ex, "...");
}
```

8 schedulers follow this shape: `LedgerPruneScheduler`, `CurrencyBalanceSyncScheduler`,
`AuditPruneScheduler`, `QuestBoardCleanupScheduler`, `QuestReminderScheduler`, `ActivityPruneScheduler`,
`BackgroundFlushScheduler`, `MultiplierBoundaryScheduler`.

## What you can answer in App Insights

- **Slow slash commands** → Performance blade, sort by duration p95. Each command is a row.
- **Error rate per command** → Failures blade, group by `operationName`.
- **A single user's last hour** → Logs blade: `requests | where customDimensions.["discord.user.id"] == "..." | order by timestamp desc`.
- **End-to-end transaction across hosts** → click a Web `POST /api/v1/...` request → see its
  `QuestLifecycleNotified` Wolverine dependency → in the same trace, the bot's consumer span fires.
- **Autocomplete p99** → Metrics: `muster.bot.autocomplete.duration` histogram, filter by `provider`.

For **container CPU / memory / replica count** — those live on the Container App's Metrics blade
(Azure Monitor platform metrics, not in App Insights). Cross-reference by timestamp.

## Health checks

Two probe surfaces (mapped by `ServiceDefaults.MapDefaultEndpoints` — web only; bot is a Worker host
without HTTP):

| Endpoint | Filter | Purpose | ACA action on fail |
| --- | --- | --- | --- |
| `/alive` | tag = `live` | Process responsive, no I/O | Restart container (Liveness probe) |
| `/health` | every check | Dependencies reachable | Gate traffic via Readiness probe (no restart) |

### ACA probe wiring (web)

`WebHostingExtensions.PublishAsAzureContainerApp` declares three probes against the ingress target port:

| Probe | Path | Period | Failure threshold | What it does |
| --- | --- | --- | --- | --- |
| **Startup** | `/alive` | 5s | 30 (= 150s grace) | Cold ASP.NET warmup + Wolverine handler graph compile. Liveness/Readiness don't evaluate until this passes once. |
| **Liveness** | `/alive` | 30s | 3 | Restarts the replica on a deadlocked process. Cheap, no I/O — can't false-trip on a DB blip. |
| **Readiness** | `/health` | 10s | 3 | Removes the replica from traffic when a dependency is unhealthy. No restart — lets the dependency recover. |

The Startup grace is intentionally generous (150s) so a low-CPU revision rollout doesn't restart-loop
during JIT + EF first-query warmup. Lower it once cold-start times are measured in prod.

Bot has no ACA probes — it's a Worker host with no HTTP listener. ACA monitors the bot via process
exit only. Health checks register in DI for in-process inspection (see below).

### Registered checks

| Check | Host | Tags | What it asserts |
| --- | --- | --- | --- |
| `self` | all | `live` | Trivially healthy — proves the HTTP pipeline is up |
| `musterdb` | web + bot | `ready` | `DbContext.CanConnectAsync()` against the Aspire-supplied connection |
| `wolverine` | web + bot | `ready` | `IWolverineRuntime.AssertHasStarted()` — endpoints + listeners + policies attached |
| `discord-gateway` | bot | `ready` | `GatewayClient.Status == WebSocketStatus.Ready`; Connecting = Degraded |

`musterdb` complements Aspire's auto-added SQL Server check: the Aspire check probes the server,
ours probes the database with the workload identity — catches "server reachable but our DB credential
expired" without us writing a query.

### Bot has no HTTP probes (yet)

The bot is a Worker (`Host.CreateApplicationBuilder`), not a `WebApplication` — `MapDefaultEndpoints`
doesn't apply. Health checks are registered in DI for in-process inspection but aren't reachable via
HTTP. ACA monitors the bot via process exit only.

If a future need surfaces (e.g. a sidecar wanting `/health`), add a tiny Kestrel listener on a
non-public port inside the bot. Checks are ready to plug in.

### Things we deliberately do NOT check

| Skipped | Why |
| --- | --- |
| KV / App Config / Storage | Workload-identity hiccups would false-alarm. Trust SDK retry; App Insights surfaces real outages. |
| Azure Service Bus | Wolverine's transport handles reconnection. Adding a check would double-report transient blips. |
| Azure SignalR Service | Marginal value — connection-string presence checked at startup, runtime issues visible in App Insights. |

## Cost guardrails

Estimated ingestion at projected scale (50 guilds, 5K users): **~2.5 GB/month**, under the 5 GB free
tier per workspace.

- **Daily cap** — set per env on the App Insights component (`az monitor app-insights component update --daily-cap`)
- **Sampling** — `UseAzureMonitor()` defaults to no sampling. Add `o.SamplingRatio = 0.25f` if traffic
  scales 5x — drops to 25% of spans, preserves p95/p99 estimates.
- **High-cardinality tags** — only on spans, never on metrics. Enforced by convention; no programmatic
  guardrail today.

## Not instrumented (yet)

Follow-ups when an operational need surfaces:

- **Gateway events** (member join, voice state, reaction add) — 9 handlers under
  `Muster.Bot.*.Handlers`. Adding spans requires per-handler wrapping; high-volume voice state would
  need sampling.
- **Gateway lifecycle** (connect/disconnect/resume) — counter `discord.gateway.reconnects` would canary
  the singleton drain (see [`deployment.md`](deployment.md) "Bot drain").
- **EF Core SQL** — `OpenTelemetry.Instrumentation.EntityFrameworkCore` would add query-level spans.
  Trade-off: chatty (every query becomes a span). Recommend only adding when investigating a
  perf issue.
