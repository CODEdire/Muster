# Persistence & schema migrations

How we evolve the database without breaking deploys.

## TL;DR

- **Backward-compatible migrations only** during normal deploys. The previous app revision must keep
  working against the new schema (in case of rollback or canary).
- **Two-deploy rule for breaking changes**: deploy the additive migration first, deploy the cleanup
  migration second, after the old revision is fully retired.
- **The migration job (`muster-migrations`) runs once per deploy**, before bot/web revisions are
  promoted to traffic. See [`docs/deployment.md`](deployment.md) "Migration job orchestration".
- **At v1 release we will start from a fresh consolidated schema** — pre-1.0 migrations get
  squashed into a single baseline migration so the prod database starts clean.

## Why backward compatibility matters

Container Apps revisions roll deployments through a brief overlap window (new replica spins up and
serves traffic before old replica drains). During that window — and during a rollback to the previous
revision — **both versions of the code must work against the schema as it exists now**. A migration
that breaks the old code = a deploy that can't be rolled back without data loss.

Even outside canary, the order of operations in a single deploy is:

```
1. Migration job runs (schema updates apply)
2. New bot/web revisions become healthy + active
3. Old revisions terminate
```

Between steps 1 and 2, the **old revision is still serving** with the **new schema** in place. If the
migration broke the old code's queries, you've broken production until step 2 finishes — and the
rollback path is unsafe.

## What's safe and what isn't

### ✅ Safe additive changes

Old code keeps working unchanged:

- **Add nullable column**
- **Add column with a default value**
- **Add new table**
- **Add index** (non-unique, or unique with no conflicts)
- **Widen column type** (`int` → `bigint`, `nvarchar(50)` → `nvarchar(100)`)
- **Add foreign key with `WITH NOCHECK`** (deferred enforcement on existing rows)
- **Add stored procedure / function / view**
- **Grant additional permissions**

### ⚠️ Conditionally safe

Requires extra care:

- **Add `NOT NULL` column** — must have a default, otherwise existing INSERTs from the old code
  fail. Prefer two-deploy: add nullable + backfill, then `ALTER COLUMN ... NOT NULL`.
- **Add unique index** — fails the migration if duplicates exist in current data.
- **Narrow a check constraint** — old code may write values now disallowed.

### ❌ Breaking changes — require expand-contract over two deploys

These all break the old revision the moment the migration runs:

- **Drop column** — old code may still read it
- **Rename column** — same as drop from the old code's perspective
- **Change column type incompatibly** (`nvarchar(100)` → `int`, `bigint` → `int`)
- **Drop table**
- **Rename table**
- **Rename or drop stored procedure / function the app calls**
- **Change foreign-key cascade behavior in ways old writes can't tolerate**

## The expand-contract recipe

For any breaking change, split into **two consecutive deploys**:

### Example: rename `Quests.Title` → `Quests.Name`

**Deploy A — additive (backward-compatible):**

```sql
-- Migration A
ALTER TABLE Quests ADD Name nvarchar(200) NULL;
GO
UPDATE Quests SET Name = Title WHERE Name IS NULL;
GO
-- Optional: trigger to keep them in sync if old code still writes Title
```

```csharp
// Code A — dual-write, prefer new for reads
quest.Title = name;  // legacy
quest.Name  = name;  // new

var display = quest.Name ?? quest.Title;  // read new with fallback
```

Both old and new revisions can run against this schema.

**Deploy B — cleanup (after Deploy A is fully rolled and stable):**

```sql
-- Migration B
ALTER TABLE Quests DROP COLUMN Title;
-- Drop the sync trigger if you added one
```

```csharp
// Code B — Name only
quest.Name = name;
var display = quest.Name;
```

Now only the new revision exists, the old column is gone, and the cleanup is done.

### Other common shapes

| Goal | Deploy A (additive) | Deploy B (cleanup) |
| --- | --- | --- |
| Rename column | Add new column + dual-write + backfill | Drop old column |
| Split column into two | Add new columns + dual-populate | Drop old column |
| Change column type | Add new-typed column + dual-populate + reads from new | Drop old column |
| Drop unused table | Code stops referencing in Deploy A | Drop table in Deploy B |
| Tighten nullability | Add default + backfill in Deploy A | `ALTER COLUMN NOT NULL` in Deploy B |

## Authoring migrations

EF Core conventions in this repo:

- Migrations live in [`src/Muster.Infrastructure/Migrations/`](../src/Muster.Infrastructure/Migrations/).
- Name them descriptively: `AddQuestCapacity`, not `Update1`.
- Generate via `dotnet ef migrations add <Name> --project src/Muster.Infrastructure`.
- Hand-edit the generated `Up`/`Down` if EF's default is unsafe (e.g. EF emits a destructive
  `DropColumn` + `AddColumn` for a type change — replace with a safe additive sequence).
- Always implement `Down` for rollback during dev; production rollback is a separate forward-fix
  deploy (we don't `Down` in production).

Before merging a migration:

1. Apply against a local dev DB with realistic data — verify no slow `ALTER` against large tables.
2. Confirm the previous revision's code still passes its tests against the new schema (run the
   integration tests on the pre-migration code branch but with the post-migration DB if you can).
3. For any change touching a table > 100k rows, schedule + document the migration runtime window
   (Azure SQL `ALTER` operations are online for most operations but `ALTER COLUMN` of large tables
   can take minutes and acquire schema-modification locks).

## Migration job runtime

- Runs as a Container Apps Job (manual trigger), see
  [`MigrationsExtensions`](../aspire/Muster.AppHost/MigrationsExtensions.cs).
- Pipeline starts it after `azd deploy` and before rolling new app revisions; failure aborts the deploy.
- Job is idempotent: `Database.MigrateAsync` is a no-op when up-to-date. Safe to re-run.
- `ReplicaRetryLimit = 0` — schema migrations are not safely retryable mid-run. Failures fail loud,
  the pipeline aborts before app revisions promote. Investigate + fix forward.

## At v1: fresh-schema baseline

Pre-1.0 migrations are scaffolding. Before the first production-ready release we'll:

1. Squash all migrations into a single `Initial` migration that creates the schema as it is at v1.
2. Drop the `__EFMigrationsHistory` rows from any non-prod databases so they pick up the new baseline.
3. Document the v1 baseline in this file (date + migration name) so post-1.0 migrations form a clean
   linear chain from a known good starting point.

This is a one-time reset. After v1, every migration follows the backward-compat rules above —
production schemas only ever move forward through reviewed, additive-or-expand-contract changes.
