# App Configuration registry

Tracks the **Azure App Configuration** keys the app reads, with their defaults and meaning. This is the source of
truth for what ops can override centrally, and the companion [`config/appconfig.defaults.json`](../config/appconfig.defaults.json)
is the importable seed.

## Precedence (how a value is resolved)

Configuration sources, lowest → highest priority (set in each host's `Program.cs`):

```
appsettings.json  <  appsettings.{Environment}.json  <  user-secrets (dev)  <  env vars  <  Key Vault  <  App Configuration
```

- **appsettings.json** — baked-in baseline / local-dev default (committed).
- **App Configuration** — central runtime override, wins over everything. Change a default across deployed
  environments without a redeploy.
- Code-level **POCO property defaults** are the final fallback when a key is absent from *every* source.

> `IOptions<T>` is singleton-cached, so an App Configuration change is picked up on the **next app start** unless
> refresh is wired (`AddAzureAppConfiguration(...).ConfigureRefresh(...)` + `IOptionsMonitor<T>`). Guild defaults
> change rarely, so plain `IOptions` + restart is fine.

## Seeding

App Configuration is **infrastructure** — seed it via provisioning or import, never from app code:

- **Local dev:** nothing to do. No App Configuration connection string → the source is skipped → POCO defaults apply.
- **Manual / one-shot:**
  ```sh
  az appconfig kv import -n <store-name> --source file --path config/appconfig.defaults.json --format json --separator :
  ```
- **Deploy (preferred, future):** add these key-values to the App Configuration resource in the AppHost provisioning
  (azd / Bicep) so they're seeded on `azd up`. Not wired yet — track here until it is.
- **Not** `MigrationService` — that owns the DB schema only; it must not write to the config store.

## Keys

Convention: `GuildDefaults:<Feature>:<Setting>` — platform-wide defaults used to **bootstrap a new guild's settings
row** (see `GuildMusterSettingsService`) and to fill reads when a guild has no row yet. Per-guild values, once an
admin sets them in the web/bot, live in the guild's own table and are unaffected by these.

### GuildDefaults:Musters  → `GuildMusterSettings`

| Key | Type | Default | Sensible as a platform default? | Notes |
|-----|------|---------|----------------------------------|-------|
| `GuildDefaults:Musters:BoardRetentionHours` | int | `48` | ✅ | Hours a terminal muster card lingers before cleanup deletes it. 0 = delete immediately. |
| `GuildDefaults:Musters:AutoCreateOnSession` | bool | `false` | ✅ | Whether opening a tracking session auto-creates + links a check-in muster (gate `Any`). |
| `GuildDefaults:Musters:CreatorAutoCheckIn` | bool | `true` | ✅ | Whether a muster's creator is auto-checked-in on create (overridable per post). |
| `GuildDefaults:Musters:DefaultExpiryHours` | int | `0` | ✅ | Default max active time before a muster auto-closes; 0 = no expiry. Templates/per-post override. |
| `GuildDefaults:Musters:DefaultMinCheckIns` | int? | _unset_ | ✅ | Default minimum check-ins before a muster pays out; unset = no minimum, 0 = an always-met minimum. Templates/per-post override. |
| `GuildDefaults:Musters:AutoCreateChannel` | enum | `DefaultChannel` | ✅ | Where an auto-created muster posts: `DefaultChannel` or `SessionChannel` (session's channel when allow-listed, else default). |
| `GuildDefaults:Musters:DefaultResolveMode` | enum | `Pay` | ✅ | What a standalone muster's auto-close does: `Pay` (close + pay now) or `Review` (hold pending for manual approval). Templates/per-post override. |
| `GuildDefaults:Musters:MusterChannelId` | ulong | `0` | ⚠️ rarely | Default card channel. Channel ids are **per-guild** — a platform-wide value almost never makes sense. Leave unset. |
| `GuildDefaults:Musters:AllowedChannelIds` | ulong[] | `[]` | ⚠️ rarely | Allowed posting channels. Per-guild; not normally a platform default. |

Only the ✅ rows belong in `appconfig.defaults.json`; the ⚠️ rows are documented for completeness but are guild-scoped.

## Adding a feature slice

As the table-per-feature migration continues (Quests, Tracking, Roles…), each new `Guild<Feature>Settings` adds a
`GuildDefaults:<Feature>` section here, its bindable knobs to the table above, and the platform-sensible ones to
`config/appconfig.defaults.json`.
