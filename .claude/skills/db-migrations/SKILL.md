---
name: db-migrations
description: Safely add, verify, apply, or remove an EF Core migration in the MCAROC portal. Use when adding a new entity/schema change, hitting a "pending model changes" error, deciding whether it's safe to run `migrations remove`, or preparing to deploy a migration.
---

Encodes a real incident (docs/ef-migrations-runbook.md — read it for the full story) where using
`dotnet ef … --no-build` right after `migrations add` caused `migrations remove` to silently delete an
already-shipped migration, and separately caused a permanent false "pending model changes" error. The
driver, `.claude/skills/db-migrations/migrate.sh`, is verified to avoid both.

All paths below are relative to the repo root.

## Use the script, not raw `dotnet ef`

```bash
bash .claude/skills/db-migrations/migrate.sh add <MigrationName>       # build, generate, verify clean
bash .claude/skills/db-migrations/migrate.sh check                    # verify no pending model changes
bash .claude/skills/db-migrations/migrate.sh apply                    # dotnet ef database update
bash .claude/skills/db-migrations/migrate.sh remove-safe <ClassName>  # safe delete of an unapplied migration
```

Verified this session: `add` (full pipeline-automation schema, 9 tables), `check` and `apply` (both clean
no-ops against an already-migrated database).

**The one rule, if you ever bypass the script and call `dotnet ef` directly:** never pass `--no-build`
unless you just ran `dotnet build` yourself in the same breath with nothing in between. That single flag is
the root cause of both failure modes in the runbook.

## One migration per feature

This repo allows only one migration branch in flight at a time. Write every entity change for the whole
feature first, then run `add` **once**. If you need to fix something before merging, use `remove-safe`
(never raw `migrations remove` — see why in the runbook §3.1), not a second migration.

## If `add` reports pending changes remain

Almost always a non-deterministic default on an entity property (`Guid.NewGuid()`, `DateTime.UtcNow`, …) —
grep your changed entities for one and replace it with a fixed default, set explicitly by the creating
service instead. Full explanation: docs/ef-migrations-runbook.md §3.2.

## Deploying

`dotnet ef database update` (no target argument) applies every pending migration in order — safe even if a
target environment's database is several migrations behind, *provided* every migration in between still
applies cleanly, which `check`+`apply` on this machine already confirms for what's committed so far. Apply
migrations before deploying the app code that depends on them. Full detail: runbook §4.
