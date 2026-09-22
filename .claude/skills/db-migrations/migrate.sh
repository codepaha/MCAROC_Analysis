#!/usr/bin/env bash
# Safe EF Core migration workflow for this repo. See docs/ef-migrations-runbook.md for the full incident
# writeup this encodes. Run from anywhere; it cd's to the app project itself.
#
# Usage:
#   bash .claude/skills/db-migrations/migrate.sh add <MigrationName>   # build, generate, verify clean
#   bash .claude/skills/db-migrations/migrate.sh check                # verify no pending model changes
#   bash .claude/skills/db-migrations/migrate.sh apply                # dotnet ef database update, verbose
#   bash .claude/skills/db-migrations/migrate.sh remove-safe <Name>   # delete a migration's OWN files by
#                                                                      # hand, never `ef migrations remove`
#
# NEVER pass --no-build to a raw `dotnet ef` command yourself outside this script — that is the root cause
# of both incidents in the runbook. This script never does either.
set -euo pipefail

SKILL_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="$(cd "$SKILL_DIR/../../../MCAROC.Portal/MCAROC_Analysis" && pwd)"
cd "$APP_DIR"

cmd="${1:-help}"

case "$cmd" in
  add)
    name="${2:?usage: migrate.sh add <MigrationName>}"
    echo "== build =="
    dotnet build --nologo
    echo "== migrations add $name (EF builds again itself — expected, do not skip) =="
    dotnet ef migrations add "$name"
    echo "== verifying no pending changes remain =="
    out="$(dotnet ef migrations has-pending-model-changes 2>&1)"
    echo "$out" | tail -3
    if echo "$out" | grep -q "No changes have been made"; then
      echo "PASS — migration '$name' fully captures the current model."
    else
      echo "FAIL — pending changes remain after generating '$name'."
      echo "See docs/ef-migrations-runbook.md §3.2: check for a non-deterministic property default"
      echo "(Guid.NewGuid(), DateTime.UtcNow, …) on any entity you just added or changed."
      exit 1
    fi
    ;;

  check)
    dotnet ef migrations has-pending-model-changes
    ;;

  apply)
    echo "== applying pending migrations to the connection string's target database =="
    dotnet ef database update
    ;;

  remove-safe)
    name="${2:?usage: migrate.sh remove-safe <MigrationName> (exact class name, e.g. AddFooSchema)}"
    file_pattern="Migrations/*_${name}.cs"
    # shellcheck disable=SC2086
    matches=($file_pattern)
    if [ ! -e "${matches[0]:-}" ]; then
      echo "No migration file matching '$name' found under Migrations/ — nothing to remove." >&2
      exit 1
    fi
    stem="${matches[0]%.cs}"
    migration_id="$(basename "$stem")"
    echo "Target: $migration_id"
    echo "== confirming this migration is NOT recorded as applied anywhere reachable from here =="
    applied="$(sqlcmd -S ".\\SQLEXPRESS" -d MCAROC_Analysis -h -1 -W \
      -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId='$migration_id'" \
      2>/dev/null | tr -d '[:space:]')"
    if [ "$applied" != "0" ]; then
      echo "REFUSING: '$migration_id' IS recorded as applied in the local database — this looks like a" >&2
      echo "migration that already shipped, not one you just added. Removing it by any method is almost" >&2
      echo "certainly wrong. Stop and read docs/ef-migrations-runbook.md §3.1." >&2
      exit 1
    fi
    echo "Confirmed unapplied locally. Deleting its two files directly (never 'ef migrations remove' —"
    echo "see the runbook for why that command can target the wrong migration entirely)."
    rm -f "${stem}.cs" "${stem}.Designer.cs"
    echo "Deleted. Now restore Migrations/AppDbContextModelSnapshot.cs to match by hand if it was already"
    echo "committed before this migration existed (git restore --worktree -- Migrations/AppDbContextModelSnapshot.cs),"
    echo "or regenerate a fresh migration once your entity changes are ready."
    ;;

  *)
    echo "usage: $0 {add <Name>|check|apply|remove-safe <Name>}"
    exit 2
    ;;
esac
