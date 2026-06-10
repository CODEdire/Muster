# Feature Gating

How Muster turns whole features on/off per guild, and how every surface must behave when a feature is off.
Introduced with the Shop; the same pattern applies to every feature as it adopts the gate.

## The gate

`IFeatureGate` (`Muster.Infrastructure.Services.Platform`) is queried like authorization and returns a
**`FeatureVerdict`** (`Muster.Contracts`):

```csharp
var verdict = await gate.EvaluateAsync(guildId, PlatformFeature.Shop, ct);
// verdict.Availability : Unavailable | Available | Enabled
// verdict.Reason       : PlatformDisabled | NotEntitled | GuildDisabled | Enabled
// verdict.IsEnabled    : Availability == Enabled
// verdict.CanEnable    : Availability != Unavailable   (platform + plan allow it)
```

Three stacked layers, **most-restrictive wins, top-down**:

| Layer | Source | Off ⇒ |
|-------|--------|-------|
| **Platform** kill-switch | `IPlatformFeatureSource` → Microsoft.FeatureManagement (flag `MusterShop` in Azure App Configuration / appsettings). Undefined flag ⇒ on. | `Unavailable` / `PlatformDisabled` |
| **Billing** entitlement | `IFeatureEntitlementSource` (stub allow-all until billing ships) | `Unavailable` / `NotEntitled` |
| **Guild** toggle | `IGuildFeatureSource` → the feature's existing per-guild switch (Shop ⇒ `GuildShopSettings.PlayerMarketEnabled`) | `Available` / `GuildDisabled` |

So:
- **Unavailable** — a layer *above the guild* blocks it. The guild can neither use nor enable it.
- **Available** — platform + plan allow it, but the guild admin switched it off. An admin can turn it on.
- **Enabled** — on and usable now.

## Per-surface policy

The behaviour depends on *which state* and *which surface*. The rule of thumb:
`CanEnable` (platform + plan OK) decides "is this hard-blocked from above?"; `IsEnabled` decides "is it on right now?".

### 1. The feature's **settings page** (admin, e.g. `/management/shop`)

Always reachable for admins (you must be able to turn the feature on). It gates its **own content**:

- The **master enable toggle** sits in a plain row **above the toolbar** (not buried in the config).
- If **`!CanEnable`** (platform/plan block) → the master toggle is **disabled/greyed with a reason** chip next to
  it ("Disabled platform-wide" / "Not included in this server's plan"); the admin can't flip what a higher layer
  overrides.
- If **not `IsEnabled`** (off, for any reason) → **hide the rest of the config** and the manage-sub-page links
  (categories, types, …) are **disabled**; show a short message: *"Enable the {feature} above to configure it."*
  (or the platform reason). The config accordion only renders when the feature is on.

### 2. Member / manager pages **not under admin** (browse, storefront, create/edit, management)

**Full-page gate** when **not `IsEnabled`** — treat like an access-denied page: a basic header + a
**"Feature not enabled"** message, **no other content**. Use the shared `<FeatureGateNotice>` component. No partial
rendering, no toolbars, no data loads.

### 3. Order/transaction **wind-down surfaces** (exception)

Surfaces whose only job is to resolve **in-flight financial state** (Shop **Orders** and the **Order receipt**)
gate **only on `!CanEnable`** (a true platform/plan block), *not* on guild-off. Rationale: when an admin merely
switches the market off, buyers/sellers/managers must still be able to confirm receipt, cancel, dispute, and
arbitrate existing escrowed orders — otherwise funds are stranded until the sweeps auto-settle. New purchases /
listings are already refused server-side, so nothing new is created. This is the **only** exception to the
full-gate rule for member pages, and it is deliberate.

### 4. Admin **sub-pages** (categories, store types, …)

Treated like member pages: **full-page gate** on **not `IsEnabled`** (same `<FeatureGateNotice>`). The settings
page's links to them are also disabled while off (defence in depth + direct-URL coverage).

### 5. Navigation

The feature's menu entry is **hidden** when **not `IsEnabled`** (evaluated in `GuildLayout`). Hiding (not a
disabled-looking item) keeps the nav clean.

### 6. Bot / API

No friendly page — the **command path is the gate**. Every feature command funnels through its authorizer
(`ShopAuthorizer`), which **blocks when `!CanEnable`** → the handler returns a failure the adapter surfaces as
**access denied / feature not enabled**. Guild-off is *not* blocked there; the service returns its own domain
result (e.g. `NotActive`) so the precise code/message — and existing tests — are preserved. **Never** move
guild-layer enforcement into the authorizer.

## Why the authorizer (not a Wolverine middleware)

Shop commands return a mix of `Result` and `Result<Guid>`; a Wolverine `Before` that stops returns `null`, which
breaks the `InvokeAsync<Result>` call sites. The authorizer is the single funnel every command already passes
through, so the gate check lives there. It blocks **only on `!CanEnable`** (platform/plan), leaving guild-off to
the service's own `NotActive` checks — which keeps result codes/messages intact.

## Implementation checklist for a new feature

1. Add a `PlatformFeature` enum member + its `PlatformFeatureNames.Of(...)` flag label (e.g. `MusterQuests`).
2. Add a case to `GuildFeatureSource` mapping it to the feature's per-guild switch.
3. Create the App Configuration flag (no label) — `enabled:false` kills it platform-wide. Restart hosts (flags
   load at startup; no live refresh wired yet).
4. Settings page: master toggle above the toolbar + content gate (policy 1).
5. Member/manager + admin sub-pages: `<FeatureGateNotice>` full gate (policies 2 & 4); wind-down surfaces use the
   `!CanEnable` soft gate (policy 3).
6. Hide the nav entry when not enabled (policy 5).
7. Enforce server-side at the feature's authorizer on `!CanEnable` (policy 6).

## Reference

- `src/Muster.Contracts/FeatureGating.cs` — enums + `FeatureVerdict`.
- `src/Muster.Infrastructure/Services/Platform/FeatureGate.cs` — gate + the three sources.
- `src/Muster.Web/Components/Shared/FeatureGateNotice.razor` — the shared full-page gate panel.
- App Configuration wiring: `src/Muster.Web/Program.cs` / `src/Muster.Bot/Program.cs` (`UseFeatureFlags()`).
