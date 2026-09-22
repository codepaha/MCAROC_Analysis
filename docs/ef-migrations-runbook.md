# EF Core migrations — runbook

How to safely add, verify, and apply an EF Core migration in this repo, and how to deploy one without
surprises. Written after a real incident while building the pipeline-automation epic (#262) — every gotcha
below was hit for real on this machine, not guessed.

## 1. The one-command safe sequence

Run these **in order, every time**, from `MCAROC.Portal/MCAROC_Analysis/`:

```bash
dotnet build                                    # 1. build first, always
dotnet ef migrations add <Name>                 # 2. generate
dotnet ef migrations has-pending-model-changes  # 3. must print "No changes have been made..."
dotnet ef database update                       # 4. apply locally, verify it actually runs
```

**Never pass `--no-build` to any `dotnet ef` command unless you are certain nothing changed since the last
build in the same breath.** This is the root cause of both incidents in §3 below — `dotnet ef … --no-build`
uses whatever DLL is already on disk, and `migrations add` only writes new **source** `.cs` files; it does
not rebuild. If you skip the rebuild and immediately run another `--no-build` command, that command is
working from an assembly that doesn't know your new migration class exists yet. Omitting `--no-build`
costs a build (EF's own tooling runs one anyway, safely, and tells you `Build started... Build succeeded.`)
and buys you correctness. If you truly want to skip rebuilding for speed, run `dotnet build` yourself first
and confirm it succeeded, then it's safe to add `--no-build` to the *next* command only.

## 2. One migration per epic/feature — how to consolidate

This repo allows only one migration branch in flight at a time (`AGENT_CHANNEL.md`'s working agreement), and
deploying several small migrations for one feature multiplies the chances of hitting §3's failure modes in
production. To land a whole feature's schema as one migration:

1. Write **every** new entity and every `OnModelCreating` change for the whole feature first, in one pass.
2. `dotnet build` to catch typos/config errors before EF ever touches migrations.
3. Run the §1 sequence exactly once — `migrations add <FeatureName>Schema` — for everything at once.
4. If you discover a missing field *after* generating but *before* merging, don't add a second migration:
   delete the generated migration's two files by hand (see §3.3 for why *not* to use `migrations remove`),
   fix the entity, rebuild, and regenerate the same migration name fresh.

## 3. Incidents — read before you touch `migrations remove`

### 3.1 `migrations remove` can delete a migration that already shipped

**What happened:** after a `dotnet ef database update` failed (see §3.2), running
`dotnet ef migrations remove --force` to "undo the bad migration I just added" instead deleted
`AddLitigationAiAnalysisLease` — a migration from `main`, already merged, already applied everywhere else —
and reverted `AppDbContextModelSnapshot.cs` to before it. It did this **twice**, identically, from two
different attempts.

**Why:** `migrations remove` decides what "the last migration" is by asking the compiled assembly which
migration types it can find via reflection — not by reading filenames or dates off disk. Because every
attempt used `--no-build` right after a fresh `migrations add` (see §3.2 — the newly-generated migration's
source file had never been compiled into the DLL yet), the assembly genuinely did not contain the new
migration class. From the tool's point of view, `AddLitigationAiAnalysisLease` really was the last migration
it knew about, so that is what it removed — correctly, by its own logic, working from stale information.

**The fix, and the rule going forward:**
- Never run `migrations remove` after a `--no-build` command. Rebuild first (`dotnet build`, no flag), same
  as §1.
- Before running `migrations remove` at all, confirm with the actual database which migration is last
  applied, and confirm your target is strictly newer:
  ```sql
  SELECT TOP 5 MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC;
  ```
  If the migration you want to remove is not visibly the newest file in `Migrations/` **and** absent from
  that query's result, stop and investigate before running `remove`.
- If in doubt, don't use `migrations remove` at all — delete the migration's own two files
  (`<timestamp>_<Name>.cs` and `<timestamp>_<Name>.Designer.cs`) by hand and `git restore` the
  `AppDbContextModelSnapshot.cs`/adjacent-migration changes it made, then regenerate. This is what recovered
  both incidents here, verified each time with `git diff --stat -- Migrations/` reporting empty before
  regenerating.

### 3.2 `PendingModelChangesWarning` that never goes away

**What happened:** `dotnet ef database update` refused to run at all, throwing
`PendingModelChangesWarning: The model … has pending changes`, immediately after a migration had just been
generated for exactly those changes. `dotnet ef migrations has-pending-model-changes` agreed. Regenerating
the migration from scratch reproduced the identical, full-size diff every time — not a small residual, the
*entire* migration's content, as if the previous one had never been recorded at all.

**Two independent causes were found, both real, both worth knowing:**

1. **The `--no-build` staleness described in §3.1** also explains this: `database update --no-build` and
   `has-pending-model-changes --no-build` were reading the same stale assembly that didn't know the new
   migration class existed, so of course it looked like nothing had been captured yet.
2. **A non-deterministic C# property default gets baked into the snapshot as a literal** — confirmed against
   [dotnet/efcore#35285](https://github.com/dotnet/efcore/issues/35285). A property declared like:
   ```csharp
   public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
   ```
   has its *default value* captured by EF's model differ as a fixed value in the migration snapshot. The
   next time you run `migrations add` (or `has-pending-model-changes`), the *newly-generated* default is a
   **different** GUID (Guid.NewGuid() runs again), so the differ sees a permanent, un-fixable mismatch —
   every future diff on that entity looks identical to the original migration, forever, no matter how many
   times you regenerate.

   **Fix:** never give a mutable entity property a non-deterministic default (`Guid.NewGuid()`,
   `DateTime.UtcNow`, `Random.Shared.Next()`, …). Default it to a fixed, empty value instead
   (`string.Empty`, `null`, `0`) and have the *service* that creates the row set the real value explicitly.
   `AutoFetchJob.CorrelationId` in this codebase already has the risky pattern
   (`= Guid.NewGuid().ToString("N")`) but has never triggered this — only because nobody has regenerated a
   migration touching that entity since EF Core's stricter pending-changes check was introduced. It is a
   live landmine, not a safe precedent to copy for a *new* entity. Don't copy it.

**How to tell which cause you're looking at:** if `has-pending-model-changes` still fails right after a
clean `dotnet build` (no `--no-build` anywhere), it's cause 2 — grep your new/changed entities for
`Guid.NewGuid()`, `DateTime.Now`/`.UtcNow`, or any other call in a property initializer, and fix it there.

### 3.3 Tool/runtime version mismatch (a real warning, but not the cause here)

`dotnet-ef migrations add` may print:
```
The Entity Framework tools version 'X' is older than that of the runtime 'Y'.
```
Fix it (`dotnet tool update --global dotnet-ef`) because a real version skew *can* cause its own subtle
differ mismatches — but don't assume fixing it will resolve a `PendingModelChangesWarning`: in this incident
the version mismatch was real and got fixed, and the actual pending-changes bug was still there afterward
(it was cause 2 in §3.2, unrelated). Treat the two as separate checks.

## 4. Deploying a migration — what the dev team needs to know

- **Never assume a target environment's database is only one migration behind.** The local dev database
  used while writing this runbook was five migrations behind `main`'s `Migrations/` folder before this
  work even started, with nobody having noticed. `dotnet ef database update` (no target argument) always
  applies every pending migration in order up to the latest, so this is safe *as long as* every migration
  in between still applies cleanly — which is exactly what §1's local verification step is for. Don't skip
  it by assuming "my migration is fine in isolation."
- **Apply migrations before deploying the new application code that depends on them**, not after and not
  as part of the same deploy step racing app startup — this app does not auto-migrate on startup (confirm
  this is still true before relying on it if that ever changes).
- **Verify with `has-pending-model-changes` in CI or right before a deploy**, not only on the machine that
  authored the migration — it catches the exact class of error in §3.2 before it reaches a shared
  environment.
- **A migration that fails to apply on a real environment is a stop-the-line event**, not something to
  route around with `migrations remove` under pressure — re-read §3.1 first; the fast path there is exactly
  how a shipped migration gets deleted from history by accident.

## 5. This epic's own migration

`AddPipelineAutomationSchema` (single migration, generated after the fix in §3.2) adds: `PipelineRuns`,
`PipelineStageStates`, `PipelineEvents` (#264), `SpendScopes`, `SpendCounters`, `PaidCallAdmissions` (#265),
`CompanyReportLifecycles`, `UnlockApprovals` (#229/#266), `IntegrationHealths` (#263), and three new nullable
columns + one filtered unique index on the existing `LitigationAiAnalysisRuns` table (#265/§4.2). Verified
locally: builds clean, `has-pending-model-changes` reports none, `database update` applies with no errors,
and all nine new tables plus the three new columns are confirmed present via a direct `sqlcmd` query against
the local database afterward.
