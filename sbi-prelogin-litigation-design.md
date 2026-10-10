# SBI Pre-Login Litigation Search — Design Specification (Rev 12)

> [!IMPORTANT]
> **Scope**: Architecture, UI design, and concurrency specification. No code changes proposed.  
> All citations against branch `feat/litigation-priority-tiering` at commit `0ffbd177`.  
> Incorporates owner decisions (D1–D5), resolves all architecture review findings, specifies shared concluded-case classifier parity across all three SQL and memory expressions (`IsDisposedExpr`, `IsPendingExpr`, `IsUnknownExpr`), enforces write-time conditional versioning across ALL `DataJson` writers (background ingestion, refresh workers, and UI edits), documents the scoped refresh-reconciliation exception to §2.4 for complete unique CNR-less proceedings, keeps ambiguous and incomplete proceedings as separate observations, prevents erroneous transfer of analyst overrides, and details the complete assignment UI.

---

## 0. Feature Summary

When a pre-login assignment is created from an Annexure / email / text, the pipeline must:

1. Extract borrower identity via AI → resolve MCA registration → fetch InstaFinancials data (existing).
2. **New (auto)**: Generate BPR litigation keywords for the **borrower entity only** → register one BPR search → poll until complete → merge results into the `.docx` report.
3. **New (manual, opt-in)**: A user may additionally trigger BPR director-name searches from the assignments UI, one director at a time. Director results do not block report generation.
4. **New (UI & Case Lake)**: A dedicated Assignment Listing and Two-Tab Detail View allowing analysts to review extracted annexure metadata, audit litigation targets, filter 100s of retrieved court cases, edit individual case details, and perform high-volume batch updates (include/exclude, status updates).

---

## 1. Owner Decisions (All Resolved)

| # | Decision | Answer |
|---|---|---|
| D1 | BPR entity type for Company / LLP borrowers | **`"company"`** |
| D2 | BPR envelope with no court-category keys | **Valid** — a completed report with only `request_details` means no litigation found. Subject to structural envelope validation (§4) and target verification (§6). |
| D3 | Auto director searches | **No.** Borrower-only auto search. Directors are manual, opt-in via UI. |
| D4 | Progress bar | **Yes.** Per-child `ProgressPercent` + `StatusMessage` in DB, surfaced in UI. |
| D5 | UI resolution of failures | **Yes — assignments UI**, not admin-only. |

---

## 2. Resolved Architecture Decisions (Carried Forward)

| # | Decision |
|---|---|
| 2.1 | Manual Partnership route (`QueuePartnershipAsync`) — **zero API calls, fully preserved.** Partnership jobs have `LitigationOnly = true`, `IsPartnership = true`; they skip MCA fetch AND litigation dispatch entirely. See [`PreLoginReportJobService.cs:197–218`](file:///e:/MCAROC_Analysis/MCAROC.Portal/MCAROC_Analysis/Services/PreLoginReports/PreLoginReportJobService.cs#L197-L218). |
| 2.2 | One auto-dispatched child per job (borrower). Director child rows are created only on explicit user action. |
| 2.3 | Settings (entity type, exact-match, keywords) are **frozen at child-row creation time** and never re-derived on retry or resume. |
| 2.4 | `CanAutoDedupe` requires non-null CNR on both sides **and** `AreCompatibleProceedingTypes`. Across general ingestion feeds, CNR-less cases are never automatically merged. [`LitigationCaseIdentity.cs:36`](file:///e:/MCAROC_Analysis/MCAROC.Portal/MCAROC_Analysis/Services/Litigation/LitigationCaseIdentity.cs#L36). **Explicit Refresh-Reconciliation Exception (§8)**: In the scoped context of re-importing/refreshing an existing assignment's report JSON, a CNR-less incoming record is matched to an existing record **only if** all 6 core identity fields (`CourtName`, `ProceedingType`, `CaseNumber`, `CaseYear`, `Petitioner`, `Respondent`) are present, non-placeholder, identical, and form a strictly unique 1-to-1 match. All other CNR-less combinations remain separate observations. |
| 2.5 | Borrower-search failure = **fatal** → parent `AwaitingReview` or `Failed`. Director-search failure = non-fatal → report generated with disclosure. |
| 2.6 | `ExactMatch` for borrower Company/LLP → `BprLitigationOptions.ExactMatchForCompanies` (default `false`). [`BprLitigationOptions.cs:40`](file:///e:/MCAROC_Analysis/MCAROC.Portal/MCAROC_Analysis/Services/Litigation/BprLitigationOptions.cs#L40) |
| 2.7 | `LitigationSearch` has **no per-call charge**. `IPaidCallAdmission` is **not** called for pre-login child targets. |
| 2.8 | Director litigation results labeled **name-match only** in the report; no PAN/DIN submitted to BPR. |
| 2.9 | All externally-reachable job lookups use `FindInBatchAsync` (batch-scoped). The worker uses `FindAsync` (internal only). See [`PreLoginReportJobService.cs:243`](file:///e:/MCAROC_Analysis/MCAROC.Portal/MCAROC_Analysis/Services/PreLoginReports/PreLoginReportJobService.cs#L243). |
| 2.10 | `AllowSoleActive = false` disables only the sole-active shortcut. A candidate meeting normal score and margin thresholds can still be auto-confirmed. |

---

## 3. Claim Predicate, Strict Lease Fencing, Authentication on Resume, and Registration Fence

Reference pattern: [`LitigationSearchJobService.cs:117–197`](file:///e:/MCAROC_Analysis/MCAROC.Portal/MCAROC_Analysis/Services/Litigation/LitigationSearchJobService.cs#L117-L197).

### Atomic Claim for `PreLoginLitigationTarget`

The claim transitions directly from `Pending → Authenticating` with zero intermediate unrecoverable state:

```sql
UPDATE PreLoginLitigationTargets
SET Status          = 'Authenticating',
    AttemptCount    = AttemptCount + 1,
    LeaseToken      = @newGuid,
    LeaseExpiresUtc = DATEADD(second, @LeaseSeconds, @now),
    StartedUtc      = COALESCE(StartedUtc, @now),
    StatusMessage   = 'Authenticating with BPR.'
WHERE TargetId      = @id
  AND Status        IN ('Pending', 'Registering', 'Polling')
  AND Status        != 'AmbiguousRegistration'
  AND (LeaseToken IS NULL OR LeaseExpiresUtc < @now)
  AND (NextAttemptUtc IS NULL OR NextAttemptUtc <= @now)
```

Verify `ExecuteUpdateAsync` returns exactly `1`. If `0`, another worker owns it or it is not ready — abort processing immediately.

### Strict Lease Guard Query Helper

```csharp
private IQueryable<PreLoginLitigationTarget> LeaseGuarded(long targetId, Guid leaseToken)
{
    var now = DateTime.UtcNow;
    return db.PreLoginLitigationTargets
        .Where(t => t.PreLoginLitigationTargetId == targetId
            && t.LeaseToken == leaseToken
            && t.LeaseExpiresUtc != null
            && t.LeaseExpiresUtc > now
            && t.Status != "Failed");
}
```

### Processing Workflow: Always Authenticate First & Strict Registration Fence

Once claimed, every worker attempt **must obtain an ephemeral BPR authentication token** before any vendor call. Furthermore, **every state transition must verify affected rows == 1** before proceeding:

```csharp
// Step 1: Always authenticate — JWT is never persisted across attempts
var token = await client.AuthenticateAsync(ct);

if (!await LeaseGuarded(targetId, leaseToken).AnyAsync(ct))
    throw new PreLoginLitigationTargetLeaseLostException(targetId);

var vendorJobId = target.VendorJobId;
if (vendorJobId is null)
{
    // Step 2a: Check for interrupted crash during registration
    if (target.RegistrationAttemptedUtc is not null)
    {
        var claimedAmbiguous = await LeaseGuarded(targetId, leaseToken).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.Status, "AmbiguousRegistration")
            .SetProperty(t => t.StatusMessage, "A previous registration attempt never confirmed; user resolution required.")
            .SetProperty(t => t.FailureReason, "Registration confirmation missing after crash. Manual reconciliation required."));

        if (claimedAmbiguous > 0)
        {
            // CRITICAL: Notify parent pipeline immediately so parent moves to AwaitingReview
            parentJobQueue.Enqueue(target.PreLoginReportJobId);
        }
        return;
    }

    // Step 2b: Registration fence — MUST VERIFY count == 1 BEFORE calling vendor
    var claimedRegistering = await LeaseGuarded(targetId, leaseToken).ExecuteUpdateAsync(s => s
        .SetProperty(t => t.Status, "Registering")
        .SetProperty(t => t.RegistrationAttemptedUtc, DateTime.UtcNow)
        .SetProperty(t => t.ProgressPercent, 15));

    if (claimedRegistering == 0)
        throw new PreLoginLitigationTargetLeaseLostException(targetId); // Lease lost; NEVER proceed to BPR!

    vendorJobId = await client.RegisterJobAsync(token, keywords, target.EntityType, ...);

    var claimedRegistered = await LeaseGuarded(targetId, leaseToken).ExecuteUpdateAsync(s => s
        .SetProperty(t => t.VendorJobId, vendorJobId)
        .SetProperty(t => t.RegisteredUtc, DateTime.UtcNow)
        .SetProperty(t => t.IsUserSuppliedVendorJobId, false) // Auto-registered provenance
        .SetProperty(t => t.ProgressPercent, 25));

    if (claimedRegistered == 0)
        throw new PreLoginLitigationTargetLeaseLostException(targetId);
}

// Step 3: Transition to polling with fresh token and confirmed vendorJobId
var claimedPolling = await LeaseGuarded(targetId, leaseToken).ExecuteUpdateAsync(s => s
    .SetProperty(t => t.Status, "Polling")
    .SetProperty(t => t.StatusMessage, "Waiting for BPR report.")
    .SetProperty(t => t.PollingDeadlineUtc, target.PollingDeadlineUtc ?? DateTime.UtcNow.AddMinutes(opts.PollTimeoutMinutes))
    .SetProperty(t => t.ProgressPercent, 30));

if (claimedPolling == 0)
    throw new PreLoginLitigationTargetLeaseLostException(targetId);

await PollUntilCompleteAsync(targetId, leaseToken, vendorJobId, token, ct);
```

> [!IMPORTANT]
> **Authentication is never skipped on resume.** A resumed search (`VendorJobId != null`) skips **registration only**, but executes authentication before issuing `GetReportAsync`.

### Recovery Sweep (`RecoverStaleWorkAsync`)

Mirrors [`LitigationSearchJobService.cs:379–407`](file:///e:/MCAROC_Analysis/MCAROC.Portal/MCAROC_Analysis/Services/Litigation/LitigationSearchJobService.cs#L379-L407):

```sql
UPDATE PreLoginLitigationTargets
SET Status = 'Pending', StatusMessage = 'Resuming after an application restart.'
WHERE Status IN ('Authenticating', 'Registering', 'Polling')
  AND (LeaseExpiresUtc IS NULL OR LeaseExpiresUtc < @now)
```

Then re-enqueues eligible `Pending` children (honoring `NextAttemptUtc`).

---

## 4. Recursive Court-Section Structural Validation

### Recursive Validation Rule in `ValidateEnvelope`

Before calling `Parse`, `ValidateEnvelope` runs strict structural checks:

1. Root must be a JSON Object.
2. `request_details.job_id` must match `expectedVendorJobId`.
3. `request_details.report_date` must be a non-empty string.
4. For every top-level property whose name is a **recognized court-category key** (`supreme_court`, `high_court`, `district_court`, `high_risk_court`, `tribunal_cases`, `defaulter_cases`), recursively validate its subtree using `ValidateCourtNode`:

```csharp
private static bool ValidateCourtNode(JsonElement node)
{
    switch (node.ValueKind)
    {
        case JsonValueKind.Object:
            if (IsCase(node))
                return true;

            // Intermediate category container (e.g. "by", "against", court, bench, state)
            foreach (var prop in node.EnumerateObject())
            {
                // Intermediate property values MUST be Object or Array
                if (prop.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                    return false; // Malformed! e.g. "civil": "error text" or "drt": 123
                if (!ValidateCourtNode(prop.Value))
                    return false;
            }
            return true;

        case JsonValueKind.Array:
            // Array of case objects or nested containers
            foreach (var item in node.EnumerateArray())
            {
                if (item.ValueKind is not JsonValueKind.Object)
                    return false; // Malformed! e.g. ["error string"]
                if (!ValidateCourtNode(item))
                    return false;
            }
            return true;

        default:
            return false;
    }
}
```

---

## 5. Branch Ordering in `ProcessAsync`

```
1. PARTNERSHIP / LITIGATION-ONLY EARLY EXIT
   if (stored?.Company.LitigationOnly == true):
       → Proceed directly to Generating
       → Never calls FetchDataAsync
       → Never creates PreLoginLitigationTarget
       → McaDataFetchedUtc is NEVER read or written

2. INTAKE PENDING
   if (BorrowerAssignmentIntake.PendingSource(job.DataJson) is not null):
       → Run AI extraction
       → Resolve identity for Company/LLP
       → If identity NOT confirmed → Status = AwaitingReview; return
       → If identity confirmed → Save DataJson, continue to step 3

3. MCA DATA FETCH
   if (job.McaDataFetchedUtc is null):
       → FetchDataAsync (InstaFinancials / MCA)
       → Serialize result into DataJson
       → Set McaDataFetchedUtc = now; save
       → Continue to step 4

4. LITIGATION CHILD DISPATCH
   if (no child target exists for this job):
       → Create PreLoginLitigationTarget (Borrower, TargetIndex=0)
   → Set Status = AwaitingLitigation; save
   → Child worker processes target

5. COMPLETION & GENERATION
   → Check borrower child status:
       - Pending/Authenticating/Registering/Polling → continue waiting
       - AmbiguousRegistration/Failed → Status = AwaitingReview (or Failed)
       - Completed → Status = Generating → Generate report .docx
```

---

## 6. Target Verification & Write-Enforced Concurrency Transactions

### Target Verification: Auto-Registered vs. User-Supplied IDs

When a BPR report is retrieved for `vendorJobId`:

1. **Auto-Registered Job IDs** (`target.IsUserSuppliedVendorJobId == false`):
   - Provenance is guaranteed: the ID was returned directly from BPR to our worker inside our lease fence.
   - If `request_details.keywords` is missing, empty, or present, the report is accepted. Empty court sections indicate a genuine empty search (T4/T20).

2. **User-Supplied Job IDs** (`target.IsUserSuppliedVendorJobId == true`):
   - A user-supplied ID has unverified provenance. It **strictly requires affirmative keyword corroboration** in the report.
   - If `request_details.keywords` is **missing, empty, or has ZERO normalized overlap** with target expected keywords (`KeywordsJson` / `TargetName`):
     - The report **cannot be corroborated**. It is rejected as `TargetCorroborationMissing`.
     - Transition child to `AmbiguousRegistration`:
       ```
       FailureReason = $"User-supplied vendor job ID {vendorJobId} could not be corroborated: BPR report does not contain matching keywords for target '{target.TargetName}'. Verification required."
       ```
     - Append reconciliation event.
     - **CRITICAL**: Enqueue `parentJobQueue.Enqueue(target.PreLoginReportJobId)` immediately so parent moves to `AwaitingReview`.

### Write-Enforced Concurrency Token Pattern

Under `ReadCommitted` isolation, reading an entity does not hold row locks. Therefore, concurrency protection **must be enforced at write time**.

1. **EF Core Model Configuration**:
   ```csharp
   modelBuilder.Entity<PreLoginLitigationTarget>()
       .Property(t => t.AttemptVersion)
       .IsConcurrencyToken();
   ```

2. **Atomic Write-Time Conditional Update in Transaction**:
   Every UI reconciliation action (`ProvideVendorJobId`, `ConfirmNoSearchExists`, `Repoll`, `Retry`) executes within an explicit database transaction (`BeginTransactionAsync(IsolationLevel.ReadCommitted)`).

   The state change is executed via an atomic conditional `ExecuteUpdateAsync` requiring `AttemptVersion == submittedAttemptVersion` and verifying affected rows == 1:

   ```csharp
   await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

   // 1. Read the current live state inside transaction to capture audit baseline
   var target = await db.PreLoginLitigationTargets.AsNoTracking()
       .FirstOrDefaultAsync(t => t.PreLoginLitigationTargetId == targetId, ct);

   if (target is null || target.Status != expectedStatus || target.AttemptVersion != submittedAttemptVersion)
   {
       await tx.RollbackAsync(ct);
       throw new PreLoginConcurrencyException("Target was modified by another operation. Please refresh the page.");
   }

   // 2. Atomic write-time conditional update matching ID, Status, AND submitted AttemptVersion:
   var affected = await db.PreLoginLitigationTargets
       .Where(t => t.PreLoginLitigationTargetId == targetId
           && t.Status == expectedStatus
           && t.AttemptVersion == submittedAttemptVersion)
       .ExecuteUpdateAsync(s => s
           .SetProperty(t => t.AttemptVersion, t => t.AttemptVersion + 1)
           .SetProperty(t => t.LeaseToken, (Guid?)null)         // Revoke stale worker lease
           .SetProperty(t => t.LeaseExpiresUtc, (DateTime?)null)
           .SetProperty(t => t.LeaseOwner, (string?)null)
           // Action-specific property mutations here...
           , ct);

   if (affected == 0)
   {
       // Simultaneous submission caught at write time!
       await tx.RollbackAsync(ct);
       throw new PreLoginConcurrencyException("Target was modified concurrently by another operation. Please refresh the page.");
   }

   // 3. Append reconciliation event within the same transaction
   var reconciliationEvent = new PreLoginLitigationReconciliationEvent
   {
       PreLoginLitigationTargetId = targetId,
       Action = actionName,
       Actor = userId,
       TimestampUtc = DateTime.UtcNow,
       PreviousStatus = target.Status,
       PreviousRegistrationAttemptedUtc = target.RegistrationAttemptedUtc,
       PreviousVendorJobId = target.VendorJobId,
       PreviousPollingDeadlineUtc = target.PollingDeadlineUtc,
       SuppliedVendorJobId = suppliedVendorJobId,
       EvidenceNote = evidenceNote,
       AttemptVersion = submittedAttemptVersion + 1
   };
   db.PreLoginLitigationReconciliationEvents.Add(reconciliationEvent);
   await db.SaveChangesAsync(ct);

   await tx.CommitAsync(ct);
   parentJobQueue.Enqueue(parentJobId);
   ```

### UI Action Definitions

1. **Provide Vendor Job ID (with Renewed Polling Window)**:
   - Precondition: `Status == 'AmbiguousRegistration'`.
   - Validates `SuppliedVendorJobId` (non-empty, alphanumeric, max 100 chars).
   - Atomic update:
     - `VendorJobId = suppliedVendorJobId`
     - `IsUserSuppliedVendorJobId = true`
     - `Status = "Polling"`
     - `PollingDeadlineUtc = DateTime.UtcNow.AddMinutes(opts.PollTimeoutMinutes)` // **Renewed deadline!**
     - `NextAttemptUtc = null` // Clear backoff
     - `AttemptCount = 0` // Reset claim counter
     - `LeaseToken = null; LeaseExpiresUtc = null; LeaseOwner = null;` // Revoke lease
     - `AttemptVersion = AttemptVersion + 1`
     - `StatusMessage = "User provided vendor job ID; polling with affirmative target corroboration."`
     - `ProgressPercent = 25`

2. **Confirm No Search Exists (Complete Fresh Reset)**:
   - Precondition: `Status == 'AmbiguousRegistration'`.
   - Requires user confirmation note.
   - Clears **all** runtime and deadline fields to prevent inheriting stale state:
     - `Status = "Pending"`
     - `VendorJobId = null`
     - `IsUserSuppliedVendorJobId = false`
     - `RegistrationAttemptedUtc = null`
     - `RegisteredUtc = null`
     - `PollingDeadlineUtc = null` // Cleared!
     - `AttemptCount = 0`
     - `NextAttemptUtc = null`
     - `LeaseToken = null; LeaseExpiresUtc = null; LeaseOwner = null;`
     - `AttemptVersion = AttemptVersion + 1`
     - `ProgressPercent = 0`
     - `StatusMessage = "Reset by operator for fresh registration."`
     - `FailureReason = null; ReportFormat = null; RawReportBytes = null; RawResponseHash = null; RawReportByteLength = null; NoLitigationFound = null; StartedUtc = null; CompletedUtc = null;`

3. **Re-poll (with Renewed Polling Window)**:
   - Precondition: `Status == 'Failed'` and `VendorJobId != null`.
   - Sets `Status = 'Polling'`, `PollingDeadlineUtc = now + PollTimeoutMinutes`, `NextAttemptUtc = null`, `AttemptCount = 0`, revokes leases, increments `AttemptVersion`.

4. **Retry Clean Failure**:
   - Precondition: `Status == 'Failed'` and `VendorJobId == null` and `RegistrationAttemptedUtc == null`.
   - Resets to `Pending`, clears attempt count, revokes leases, increments `AttemptVersion`.

---

## 7. Polling Deadlines, Scheduling, and Retries

- **Auto-polling**: `PollingDeadlineUtc` initialized upon entering `Polling` to `RegisteredUtc.AddMinutes(opts.PollTimeoutMinutes)`. Preserved during worker backoff retries.
- **User Re-poll & Provide Vendor Job ID**: Explicitly grants a renewed `PollingDeadlineUtc = DateTime.UtcNow.AddMinutes(opts.PollTimeoutMinutes)`.
- **Confirm No Search**: Resets `PollingDeadlineUtc = null`.
- **Parent Queue Signal**: Child worker enqueues `parentJobQueue.Enqueue(target.PreLoginReportJobId)` on **ALL** terminal and review-gated transitions (`Completed`, `Failed`, `AmbiguousRegistration`).

---

## 8. Target Entity & Concurrency Schemas

### Parent Job Concurrency Schema (`PreLoginReportJob`)
To prevent concurrent overwrite of `DataJson` between simultaneous analyst edits and background workers (initial ingestion, litigation completion merges, refresh sweeps):

```csharp
public sealed class PreLoginReportJob
{
    // ... existing fields ...
    public string? DataJson { get; set; }
    
    // Concurrency token for report JSON modifications:
    public int DataVersion { get; set; } = 1;
}
```

EF Core mapping in `AppDbContext.cs`:
```csharp
modelBuilder.Entity<PreLoginReportJob>()
    .Property(j => j.DataVersion)
    .IsConcurrencyToken();
```

#### Mandatory Rule: Version Every `DataJson` Writer
Because EF Core's `ExecuteUpdateAsync` bypasses entity tracking and does not automatically check or increment concurrency tokens, and standard `SaveChangesAsync` only checks concurrency without automatic version increments on raw SQL or disconnected payloads:

**EVERY writer that mutates `job.DataJson` (UI single-edit, UI batch-update, background ingestion merge, and background refresh workers) MUST execute an atomic conditional write requiring matching `DataVersion` and incrementing `DataVersion = DataVersion + 1`:**

```csharp
// Standard conditional update pattern for ALL DataJson writers:
var affected = await db.PreLoginReportJobs
    .Where(j => j.PreLoginReportJobId == id
        && j.BatchId == batchId
        && j.DataVersion == expectedDataVersion)
    .ExecuteUpdateAsync(s => s
        .SetProperty(j => j.DataJson, newSerializedJson)
        .SetProperty(j => j.DataVersion, j => j.DataVersion + 1), ct);

if (affected == 0)
{
    await tx.RollbackAsync(ct);
    throw new PreLoginConcurrencyException("Concurrent modification detected on assignment data. Operation aborted.");
}
```

If a background worker encounters `affected == 0`, it re-reads the latest entity, merges new external data against the newly incremented `DataVersion`, and retries. If a UI analyst submission encounters `affected == 0`, it prompts the user to refresh their view rather than silently overwriting refreshed data.

---

### Stable Assignment-Scoped Case Identifier (`CaseRowId`) Allocation & Refresh Reconciliation

Because tribunal and district records frequently have null CNRs, and distinct proceedings can share a CNR or filing number, case identity relies on stable `CaseRowId` values.

```csharp
public sealed class LegalCaseRecord
{
    /// <summary>
    /// Stable assignment-scoped row identifier: "C-1", "C-2", etc.
    /// Immutable once assigned; preserved across all subsequent data refreshes and re-sorts.
    /// </summary>
    public string CaseRowId { get; set; } = string.Empty;
    public string? Cnr { get; set; }
    public string? CaseNumber { get; set; }
    public string? ProceedingType { get; set; }
    public int? CaseYear { get; set; }
    public string? CourtName { get; set; }
    public string? Petitioner { get; set; }
    public string? Respondent { get; set; }
    public string? CaseStatus { get; set; }
    public string? CaseStage { get; set; }
    public bool IsExcludedFromReport { get; set; }
    public string? ExclusionReason { get; set; }
    public string? AnalystNote { get; set; }
    // ... remaining case metadata ...
}
```

#### 1. Initial Allocation
When a litigation payload is first parsed into `InstaReportData.LegalCases.Cases`:
- IDs are generated sequentially: `$"C-{1}", $"C-{2}", ...` up to `$"C-{N}"`.
- A monotonic counter `NextCaseRowIdIndex = N + 1` is persisted in `InstaReportData.LegalCases`.
- No duplicate `CaseRowId` values are permitted within an assignment; an invariant check asserts uniqueness upon serialization.

#### 2. Re-import, Refresh, and Reordering Reconciliation Algorithm
When subsequent data arrives (e.g. background polling completion, director searches, manual re-polling, or vendor refresh) where vendor cases may be returned in a different order or contain added proceedings:

To strictly prevent transferring an analyst's exclusion reasons or notes onto the wrong case, and in full compliance with **§2.4** (`LitigationCaseIdentity.cs:36`, which dictates that cases lacking sufficient decisive identity evidence stay as separate observations), refresh reconciliation enforces the following rules:

1. **Decisive One-to-One Match Rule**:
   - An incoming vendor case matches an existing `LegalCaseRecord` **if and only if** there is a unique, unambiguous, one-to-one match meeting decisive identity evidence:
     - **CNR Match**: Valid, shared 16-character CNR (`left.Cnr == right.Cnr`) **AND** compatible proceeding types (`AreCompatibleProceedingTypes(left.ProceedingType, right.ProceedingType)` per `LitigationCaseIdentity.CanAutoDedupe`).
     - **Strict Non-CNR / Court Proceeding Match**: If both sides lack CNR, an automatic match is permitted **ONLY IF ALL of the following conditions are strictly satisfied**:
       1. All core identity fields are non-empty and non-placeholder: `CourtName`, `ProceedingType`, `CaseNumber`, `CaseYear`, `Petitioner`, and `Respondent`. If **ANY** of these fields is missing, null, or placeholder (e.g. `"NA"`, `"-"`, `"NULL"` per `LitigationCaseIdentity.IsPlaceholder`), automatic matching is rejected.
       2. Normalised values match exactly across all six fields.
       3. The match is **strictly unique (1-to-1)**: exactly one existing case matches the incoming record, and no other incoming case matches the same existing case.
2. **Ambiguous or Incomplete Matches Retained as Separate Observations**:
   - If an incoming record matches multiple existing records, or if an existing record matches multiple incoming records (e.g. identical party names across multiple interlocutory or connected matters), automatic merging is **strictly prohibited**.
   - If core identity evidence is incomplete (missing proceeding type, null case number, or missing year), automatic merging is **strictly prohibited**.
   - In all such ambiguous or incomplete cases, the incoming record is treated as a **separate observation** and allocated a new, distinct `CaseRowId` (`$"C-{NextCaseRowIdIndex++}"`).
   - **Zero Cross-Contamination Guarantee**: Under NO circumstances are analyst exclusions (`IsExcludedFromReport`), exclusion reasons, or analyst notes transferred across ambiguous, incomplete, or different proceeding types.
3. **Preservation of Existing IDs and Analyst Overrides on Confirmed 1-to-1 Matches**:
   - For confirmed 1-to-1 matches: **Retain existing `CaseRowId`**.
   - **Preserve analyst state**: `IsExcludedFromReport`, `ExclusionReason`, and `AnalystNote` are carried over from the existing record.
   - **Preserve concluded status**: If existing case satisfied `IsConcluded(CaseStatus, CaseStage)`, its status/stage is preserved per §11.6.
4. **Allocation for New Records**:
   - Any unmatched or separate-observation incoming case is allocated the next available ID from the monotonic counter: `$"C-{NextCaseRowIdIndex++}"`.
5. **Order Invariance**:
   - Sorting, re-indexing, or filtering of the array in UI or storage does NOT alter assigned `CaseRowId` strings.
6. **Reconciliation Validation**:
   - If an incoming payload contains duplicate `CaseRowId` values or attempts to reuse an existing ID for an unmatched proceeding, the import transaction aborts and throws `InvalidDataException("Duplicate or conflicting CaseRowId detected during reconciliation.")`.

### Child Target Schema (`PreLoginLitigationTarget`)

```csharp
public sealed class PreLoginLitigationTarget
{
    public long PreLoginLitigationTargetId { get; set; }
    public long PreLoginReportJobId { get; set; }
    public PreLoginReportJob? PreLoginReportJob { get; set; }

    public PreLoginTargetKind TargetKind { get; set; }
    public int TargetIndex { get; set; }
    public string TargetName { get; set; } = string.Empty;

    public string EntityType { get; set; } = string.Empty;
    public bool ExactMatch { get; set; }
    public string KeywordsJson { get; set; } = "[]";

    public string? VendorJobId { get; set; }
    public bool IsUserSuppliedVendorJobId { get; set; }
    public DateTime? RegistrationAttemptedUtc { get; set; }
    public DateTime? RegisteredUtc { get; set; }

    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }

    public string Status { get; set; } = "Pending";
    public int AttemptCount { get; set; }
    public int AttemptVersion { get; set; } = 1;
    public DateTime? NextAttemptUtc { get; set; }
    public DateTime? PollingDeadlineUtc { get; set; }
    public int ProgressPercent { get; set; }
    public string? StatusMessage { get; set; }
    public string? FailureReason { get; set; }

    public string? ReportFormat { get; set; }
    public byte[]? RawReportBytes { get; set; }
    public string? RawResponseHash { get; set; }
    public long? RawReportByteLength { get; set; }
    public bool? NoLitigationFound { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }

    public ICollection<PreLoginLitigationReconciliationEvent> ReconciliationEvents { get; set; } = [];
}

public sealed class PreLoginLitigationReconciliationEvent
{
    public long PreLoginLitigationReconciliationEventId { get; set; }
    public long PreLoginLitigationTargetId { get; set; }
    public PreLoginLitigationTarget? Target { get; set; }

    public string Action { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }

    public string PreviousStatus { get; set; } = string.Empty;
    public DateTime? PreviousRegistrationAttemptedUtc { get; set; }
    public string? PreviousVendorJobId { get; set; }
    public DateTime? PreviousPollingDeadlineUtc { get; set; }
    public string? SuppliedVendorJobId { get; set; }
    public string? EvidenceNote { get; set; }
    public int AttemptVersion { get; set; }
}

public enum PreLoginTargetKind { Borrower, Director }
```

**Table Constraints**:
```sql
UNIQUE (PreLoginReportJobId, TargetKind, TargetIndex)
```

---

## 9. Implementation Checklist

| File | Change |
|---|---|
| [`PreLoginReportJob.cs`](file:///e:/MCAROC_Analysis/MCAROC.Portal/MCAROC_Analysis/Data/Entities/PreLoginReportJob.cs) | Add `McaDataFetchedUtc DateTime?`; add `DataVersion int = 1` concurrency token; add `LitigationTargets` nav |
| `PreLoginLitigationTarget.cs` | **New file** — entity as specified in §8 |
| `PreLoginLitigationReconciliationEvent.cs` | **New file** — append-only entity as specified in §8 |
| [`PreLoginReportJobStatus`](file:///e:/MCAROC_Analysis/MCAROC.Portal/MCAROC_Analysis/Data/Entities/PreLoginReportJob.cs#L3) | Add `AwaitingLitigation` |
| [`AppDbContext.cs`](file:///e:/MCAROC_Analysis/MCAROC.Portal/MCAROC_Analysis/Data/AppDbContext.cs) | Register target and event DbSets, foreign keys, unique constraint, `.IsConcurrencyToken()` on `AttemptVersion` (target) and `DataVersion` (job) |
| [`LitigationTabViewModel.cs`](file:///e:/MCAROC_Analysis/MCAROC.Portal/MCAROC_Analysis/Models/LitigationTabViewModel.cs#L27) | Extend `LitigationCaseStatusClassifier` to recognize `transfer` and `compromis` across SQL and memory predicates (§11.3) |
| [`BprLitigationOptions.cs`](file:///e:/MCAROC_Analysis/MCAROC.Portal/MCAROC_Analysis/Services/Litigation/BprLitigationOptions.cs) | Document `"company"` as standard for pre-login borrower searches |
| [`PreLoginReportJobService.cs`](file:///e:/MCAROC_Analysis/MCAROC.Portal/MCAROC_Analysis/Services/PreLoginReports/PreLoginReportJobService.cs) | Pipeline branch order (§5), UI reconciliation endpoints with write-enforced concurrency (§6), Case batch update endpoints with `DataVersion` conditional write and `CaseRowId` targeting (§11) |
| [`PreLoginReportWorker.cs`](file:///e:/MCAROC_Analysis/MCAROC.Portal/MCAROC_Analysis/Services/PreLoginReports/PreLoginReportWorker.cs) | Startup recovery re-enqueues `AwaitingLitigation` parents |
| `PreLoginLitigationTargetService.cs` | **New file** — Worker with strict lease check before vendor calls, always-authenticate resume, parent queue wakeups on Ambiguous/Failed/Completed |
| [`BprLitigationReportParser.cs`](file:///e:/MCAROC_Analysis/MCAROC.Portal/MCAROC_Analysis/Services/Litigation/BprLitigationReportParser.cs) | Add `ValidateEnvelope` with recursive court-section structural validation (§4) and target corroboration verification (§6) |
| Assignments UI | Assignment Listing Page, Two-Tab Detail View, Single Case Modal, and High-Volume Batch Action Toolbar (§11) |

---

## 10. Concrete Acceptance Tests

| # | Scenario | Input State | Expected Outcome | Design Rule |
|---|---|---|---|---|
| T1 | Crash recovery for in-flight claim | Child `Status='Authenticating'`, `LeaseExpiresUtc < now` | `RecoverStaleWorkAsync` resets to `Pending`; next claim succeeds without manual intervention. | §3 |
| T2 | Ambiguous registration after crash | Child `Status='Registering'`, lease expired, `VendorJobId=null`, `RegistrationAttemptedUtc` set | Claim detects set `RegistrationAttemptedUtc` with null `VendorJobId` → marks `AmbiguousRegistration`. Parent enqueued immediately. | §3 + §7 |
| T3 | Top-level malformed court section | BPR returns `{"request_details":{...},"high_court":"error"}` | `ValidateEnvelope` returns `MalformedCourtSection`; child throws retryable error; zero cases NOT accepted. | §4 |
| T4 | Genuine empty report accepted (Auto) | BPR returns `{"request_details":{"job_id":"J","report_date":"D"}}` on auto-registered search | `ValidateEnvelope` returns `NoLitigation`; `NoLitigationFound=true`; report displays "No litigation found". | §4 + §6 |
| T5 | Partnership job bypasses MCA & litigation | Partnership job `IsLitigationOnly=true, IsPartnership=true` | Proceeds straight to report generation; zero external calls; `McaDataFetchedUtc` not evaluated. | §5 |
| T6 | Non-partnership LitigationOnly bypasses MCA | `IsLitigationOnly=true, IsPartnership=false` | Same as T5 — LitigationOnly early-exit precedes MCA check. | §5 |
| T7 | Double MCA fetch prevented | Second attempt on corporate job; `McaDataFetchedUtc` non-null | `FetchDataAsync` is skipped; worker advances straight to child dispatch. | §5 |
| T8 | Parent woken upon child completion | Borrower child transitions to `Completed` | Worker enqueues parent ID into `PreLoginReportQueue`; parent advances to `Generating` without polling delay. | §7 |
| T9 | Lease claims distinct from poll loops | Worker performs 10 polling intervals within one lease | `AttemptCount` remains 1; increments only on new claim following transient worker crash. | §7 |
| T10 | Multi-tenant batch security on UI reconciliation | Action submitted with mismatched `BatchId` | `FindInBatchAsync` returns null → HTTP 403 Forbidden; target unaffected. | §6 |
| T11 | Single high-scoring LLP candidate auto-selection | Resolver evaluates single candidate with score 1.0, margin ≥ 0.10, `AllowSoleActive=false` | `Decide` returns `Resolved`; auto-confirmed without manual review. | §2 (2.10) |
| T12 | Resumed search authenticates before polling | Resumed search with existing `VendorJobId` | Claims job → calls `AuthenticateAsync` to obtain fresh JWT → calls `GetReportAsync` with new token. Skips registration only. | §3 |
| T13 | Nested malformed court section rejected | BPR returns `{"request_details":{...},"high_court":{"against":{"civil":"error text"}}}` | `ValidateCourtNode` detects intermediate string primitive → returns `MalformedCourtSection`; rejects payload. | §4 |
| T14 | Foreign Vendor Job ID rejected by target verification | User supplies valid job ID; BPR report has `keywords: ["UNRELATED CORP"]` | Target verification detects zero overlap with target keywords → rejects report with `TargetMismatch` → transitions to `AmbiguousRegistration`. | §6 |
| T15 | User Re-poll grants renewed timeout window | Target timed out (`Status='Failed'`, `VendorJobId!=null`); user clicks Re-poll | `PollingDeadlineUtc` renewed to `now + PollTimeoutMinutes`; `AttemptCount=0`; worker resumes polling with full window. | §6 + §7 |
| T16 | Append-only reconciliation provenance | Target undergoes "Confirm No Search Exists", crashes, then receives "Provide Vendor Job ID" | Both reconciliation events are durably recorded in `PreLoginLitigationReconciliationEvents` table; full actor, timestamp, and previous state history preserved. | §6 |
| T17 | "Confirm No Search Exists" clears intent without fabricating empty results | Operator confirms no search exists with BPR | `RegistrationAttemptedUtc` and `VendorJobId` cleared to null; status set to `Pending`; `NoLitigationFound` remains null; re-registers clean. | §6 |
| T18 | Fenced registration aborts if lease lost before vendor call | Worker attempts `Registering` update; lease expired; `claimedRegistering == 0` | Throws `PreLoginLitigationTargetLeaseLostException`; `client.RegisterJobAsync` is NEVER called. | §3 |
| T19 | User-supplied VendorJobId requires keyword corroboration | User supplies job ID (`IsUserSupplied=true`); BPR report has null/empty keywords | Report rejected as `TargetCorroborationMissing`; transitions to `AmbiguousRegistration`; parent enqueued. | §6 |
| T20 | Auto-registered VendorJobId accepts report without keywords | Auto-registered job (`IsUserSupplied=false`); BPR returns report with empty keywords | Report accepted as valid empty report (`NoLitigationFound=true`) due to trusted registration provenance. | §6 |
| T21 | Ambiguous child immediately wakes parent pipeline | Child transitions to `AmbiguousRegistration` | Worker enqueues parent ID into `PreLoginReportQueue`; parent moves from `AwaitingLitigation` to `AwaitingReview` immediately. | §3 + §7 |
| T22 | Confirm No Search clears polling deadline in atomic transaction | Operator confirms no search | `PollingDeadlineUtc` cleared to null; runtime artifacts reset; audit event saved; all in one transaction. | §6 |
| T23 | Stale UI reconciliation screen rejected by version check | User submits resolution with stale `AttemptVersion` (target updated concurrently) | Concurrency conflict exception thrown; transaction rolls back; target state unaffected. | §6 |
| T24 | Provide Vendor Job ID grants renewed polling deadline | User supplies corrected VendorJobId after old deadline expired | `PollingDeadlineUtc` renewed to `now + PollTimeoutMinutes`; `NextAttemptUtc` cleared; `LeaseToken` revoked; polls without immediate timeout. | §6 |
| T25 | Simultaneous-submission write-time concurrency race | Concurrent requests A and B both read `AttemptVersion = 1` for same target | Request A conditional update matches 1 row, commits, increments version to 2 with audit event. Request B conditional update matches 0 rows (`WHERE AttemptVersion = 1`), throws concurrency exception, rolls back transaction, and creates NO audit event. | §6 |
| T26 | Concluded case update protection across status and stage | Batch status update (`NewStatus="Stayed"`) targeting cases where some have `Status="Pending"` but `CaseStage="Disposed"`, `CaseStage="Transferred"`, or `CaseStatus="Compromised"` | All cases satisfying `IsConcluded(Status, CaseStage)` in either status OR stage remain strictly protected and unchanged; only non-concluded cases are updated; response skips all concluded records. | §11.3 + §11.4 + §11.5 |
| T27 | Assignment case edits write-time concurrency race (`DataVersion`) | Concurrent analyst edits A and B both read assignment `DataVersion = 3` and attempt case updates | Edit A conditional update matches `DataVersion = 3`, sets `DataVersion = 4`, and commits. Edit B conditional update matches 0 rows (`WHERE DataVersion = 3`), rolls back transaction, throws `PreLoginConcurrencyException`, and preserves Edit A's changes without silent overwrite. | §8 + §11.5 |
| T28 | `CaseRowId` stability, refresh reordering, successful unique CNR-less match, and zero override cross-contamination | Assignment has 20 cases with analyst exclusions on `C-3` (`CNR="DLI001...", ProceedingType="CS"`) and `C-7` (CNR-less, `ProceedingType="CP"`, `CaseNumber="55/2022"`, `CourtName="NCLT"`, `CaseYear=2022`, `Petitioner="Bank"`, `Respondent="Target"`). Vendor refresh returns 25 cases in completely reversed sort order, containing: (1) reversed-order `C-3` CNR match, (2) reversed-order `C-7` unique complete CNR-less match, (3) an incoming case with identical parties to `C-7` but `ProceedingType="IA"` (different type), (4) an incoming case with missing `CaseNumber` (missing field), and (5) two incoming cases with identical CNR-less tuples (ambiguous match) | Reversed `C-3` and `C-7` both cleanly preserve their `CaseRowId`s and analyst exclusions (`C-7` via the explicit §8 refresh exception for unique complete CNR-less tuples); the different proceeding type (`IA`), incomplete record (missing number), and two ambiguous duplicates are strictly NOT merged and do NOT inherit `C-7`'s exclusion; all four are allocated new separate observation IDs (`C-21` through `C-24`); zero analyst overrides transferred to wrong cases. | §2.4 + §8 |
| T29 | Background worker vs. UI analyst edit `DataVersion` collision | Background litigation merge reads assignment `DataVersion = 2`; analyst concurrently submits bulk case exclusion with `SubmittedDataVersion = 2` | Whichever writer commits first increments `DataVersion` to 3. The second writer's conditional update (`WHERE DataVersion = 2`) matches 0 rows, rolls back, and triggers clean retry (background worker) or user conflict notification (analyst UI) without silent data overwrite. | §8 + §11.5 |
| T30 | Classifier triad SQL and in-memory mutual exclusivity & parity | Cases tested with combinations: `("Transferred", "Hearing")`, `("Pending", "Compromised")`, `("Arbitrary", "Notice")`, `("", "")` | Evaluated across both LINQ SQL expressions (`IsDisposedExpr`, `IsPendingExpr`, `IsUnknownExpr`) and in-memory methods (`Classify`, `IsConcluded`); exact parity achieved with zero overlap; `Transferred` and `Compromised` classify strictly as Disposed in both SQL and memory. | §11.3 |

---

## 11. Assignment UI Architecture: Listing, Detail View & High-Volume Litigation Case Management

### 11.1 Assignment Listing Page (`/Assignments`)

The primary landing interface for pre-login assignments:

- **Filter Bar**: Batch selection, Date range, Status filter (`All`, `Queued`, `AwaitingLitigation`, `AwaitingReview`, `Generating`, `Completed`, `Failed`), Text search (Borrower Name, CIN/LLPIN).
- **Assignments Table**:
  - **Reference**: Batch ID / Identifier.
  - **Borrower Name & Type**: Legal corporate name + Entity badge (`Pvt Ltd`, `Public Ltd`, `LLP`, `Partnership`).
  - **Request Details**: Date of Request, SBI Branch Code & Name.
  - **Status & Progress**: Badge + progress bar (e.g. `Fetching MCA`, `Polling Litigation (60%)`, `Awaiting Review`, `Completed`).
  - **Litigation Indicator**: Shows retrieved case counts (e.g., `14 Cases` or `No Litigation Found`).
  - **Actions**:
    - **"View" Button**: Navigates to Assignment Detail View (`/Assignments/View/{batchId}/{id}`).
    - **"Download Report"**: One-click DOCX download when `Completed`.

---

### 11.2 Assignment Detail View (`/Assignments/View/{batchId}/{id}`)

A focused two-tab workspace for analyzing and editing the assignment:

#### Top Summary Header
- Borrower Legal Name, CIN/LLPIN, Current Status, Report Download button, "Regenerate Report" button.

#### Tab 1: Annexure & Borrower Details
1. **Requesting Branch Details**: Document Title, Date of Request, SBI Branch Name, Branch Code, Address, Contact Person (Name, Mobile, Email).
2. **Corporate & Registration Profile**: Legal Name, Entity Type, CIN/LLPIN, PAN, GSTIN, Incorporation Date, Registered Office Address.
3. **Directors & Partners Inventory**:
   - Tabular list: Name, DIN, PAN, Designation.
   - **"Search Litigation" Button**: On-demand action per director to launch an individual name search in BPR Litigation Lake.
4. **Litigation Search Progress & Reconciliation Card**:
   - Real-time progress bar with current status message and attempt count.
   - When in `AmbiguousRegistration`: Warning panel with two action buttons:
     - **"Provide Vendor Job ID"**: Opens modal to input confirmed BPR job ID.
     - **"Confirm No Search Exists"**: Opens modal requiring operator confirmation note to reset for a clean registration.
   - When in `Failed`: Action buttons for **"Re-poll"** (renewed window) and **"Retry"**.

---

### 11.3 Tab 2: Related Legal Cases & Litigation Lake

A comprehensive management interface for all court cases associated with the assignment:

#### Summary KPI Cards
Top overview cards showing counts bucketed by forum:
- **Total Cases**: Total discovered records.
- **High Court**: Total High Court cases.
- **District Court**: Total civil/criminal district cases.
- **DRT & Tribunals**: DRT, NCLT, NCLAT, Consumer forum cases.
- **Included in Report**: Count of cases currently enabled for inclusion in the generated SBI report.
- **Excluded**: Count of cases excluded by analyst review.

#### Concluded Case Classifier: Complete Three-Expression Triad & Parity Specification
To ensure strict consistency across EF Core SQL queries and in-memory evaluation, all three mutually exclusive expressions in `LitigationCaseStatusClassifier` (`IsDisposedExpr`, `IsPendingExpr`, `IsUnknownExpr`) are updated together. Omitting `transfer` or `compromis` from the negative exclusion clauses of `IsPendingExpr` or `IsUnknownExpr` would cause cases like `CaseStatus="Transferred", CaseStage="Hearing"` to erroneously match both `Disposed` and `Pending` in SQL queries.

```csharp
public static class LitigationCaseStatusClassifier
{
    // Concluded tokens: dispos, clos, dismis, withdr, settl, decid, quash, decree, allow, reject, transfer, compromis
    public static readonly Expression<Func<LitigationCase, bool>> IsDisposedExpr = c =>
        (c.CaseStatus != null && (
            c.CaseStatus.ToLower().Contains("dispos") || c.CaseStatus.ToLower().Contains("clos") ||
            c.CaseStatus.ToLower().Contains("dismis") || c.CaseStatus.ToLower().Contains("withdr") ||
            c.CaseStatus.ToLower().Contains("settl")  || c.CaseStatus.ToLower().Contains("decid") ||
            c.CaseStatus.ToLower().Contains("quash")  || c.CaseStatus.ToLower().Contains("decree") ||
            c.CaseStatus.ToLower().Contains("allow")  || c.CaseStatus.ToLower().Contains("reject") ||
            c.CaseStatus.ToLower().Contains("transfer") || c.CaseStatus.ToLower().Contains("compromis")
        )) || (c.CaseStage != null && (
            c.CaseStage.ToLower().Contains("dispos") || c.CaseStage.ToLower().Contains("clos") ||
            c.CaseStage.ToLower().Contains("dismis") || c.CaseStage.ToLower().Contains("withdr") ||
            c.CaseStage.ToLower().Contains("settl")  || c.CaseStage.ToLower().Contains("decid") ||
            c.CaseStage.ToLower().Contains("quash")  || c.CaseStage.ToLower().Contains("decree") ||
            c.CaseStage.ToLower().Contains("allow")  || c.CaseStage.ToLower().Contains("reject") ||
            c.CaseStage.ToLower().Contains("transfer") || c.CaseStage.ToLower().Contains("compromis")
        ));

    public static readonly Expression<Func<LitigationCase, bool>> IsPendingExpr = c =>
        !(
            (c.CaseStatus != null && (
                c.CaseStatus.ToLower().Contains("dispos") || c.CaseStatus.ToLower().Contains("clos") ||
                c.CaseStatus.ToLower().Contains("dismis") || c.CaseStatus.ToLower().Contains("withdr") ||
                c.CaseStatus.ToLower().Contains("settl")  || c.CaseStatus.ToLower().Contains("decid") ||
                c.CaseStatus.ToLower().Contains("quash")  || c.CaseStatus.ToLower().Contains("decree") ||
                c.CaseStatus.ToLower().Contains("allow")  || c.CaseStatus.ToLower().Contains("reject") ||
                c.CaseStatus.ToLower().Contains("transfer") || c.CaseStatus.ToLower().Contains("compromis")
            )) || (c.CaseStage != null && (
                c.CaseStage.ToLower().Contains("dispos") || c.CaseStage.ToLower().Contains("clos") ||
                c.CaseStage.ToLower().Contains("dismis") || c.CaseStage.ToLower().Contains("withdr") ||
                c.CaseStage.ToLower().Contains("settl")  || c.CaseStage.ToLower().Contains("decid") ||
                c.CaseStage.ToLower().Contains("quash")  || c.CaseStage.ToLower().Contains("decree") ||
                c.CaseStage.ToLower().Contains("allow")  || c.CaseStage.ToLower().Contains("reject") ||
                c.CaseStage.ToLower().Contains("transfer") || c.CaseStage.ToLower().Contains("compromis")
            ))
        ) && (
            (c.CaseStatus != null && (
                c.CaseStatus.ToLower().Contains("pend")  || c.CaseStatus.ToLower().Contains("admit") ||
                c.CaseStatus.ToLower().Contains("hear")  || c.CaseStatus.ToLower().Contains("stage") ||
                c.CaseStatus.ToLower().Contains("evid")  || c.CaseStatus.ToLower().Contains("argum") ||
                c.CaseStatus.ToLower().Contains("notic") || c.CaseStatus.ToLower().Contains("stay") ||
                c.CaseStatus.ToLower().Contains("trial") || c.CaseStatus.ToLower().Contains("appear")
            )) || (c.CaseStage != null && (
                c.CaseStage.ToLower().Contains("pend")  || c.CaseStage.ToLower().Contains("admit") ||
                c.CaseStage.ToLower().Contains("hear")  || c.CaseStage.ToLower().Contains("stage") ||
                c.CaseStage.ToLower().Contains("evid")  || c.CaseStage.ToLower().Contains("argum") ||
                c.CaseStage.ToLower().Contains("notic") || c.CaseStage.ToLower().Contains("stay") ||
                c.CaseStage.ToLower().Contains("trial") || c.CaseStage.ToLower().Contains("appear")
            ))
        );

    public static readonly Expression<Func<LitigationCase, bool>> IsUnknownExpr = c =>
        !(
            (c.CaseStatus != null && (
                c.CaseStatus.ToLower().Contains("dispos") || c.CaseStatus.ToLower().Contains("clos") ||
                c.CaseStatus.ToLower().Contains("dismis") || c.CaseStatus.ToLower().Contains("withdr") ||
                c.CaseStatus.ToLower().Contains("settl")  || c.CaseStatus.ToLower().Contains("decid") ||
                c.CaseStatus.ToLower().Contains("quash")  || c.CaseStatus.ToLower().Contains("decree") ||
                c.CaseStatus.ToLower().Contains("allow")  || c.CaseStatus.ToLower().Contains("reject") ||
                c.CaseStatus.ToLower().Contains("transfer") || c.CaseStatus.ToLower().Contains("compromis")
            )) || (c.CaseStage != null && (
                c.CaseStage.ToLower().Contains("dispos") || c.CaseStage.ToLower().Contains("clos") ||
                c.CaseStage.ToLower().Contains("dismis") || c.CaseStage.ToLower().Contains("withdr") ||
                c.CaseStage.ToLower().Contains("settl")  || c.CaseStage.ToLower().Contains("decid") ||
                c.CaseStage.ToLower().Contains("quash")  || c.CaseStage.ToLower().Contains("decree") ||
                c.CaseStage.ToLower().Contains("allow")  || c.CaseStage.ToLower().Contains("reject") ||
                c.CaseStage.ToLower().Contains("transfer") || c.CaseStage.ToLower().Contains("compromis")
            ))
        ) && !(
            (c.CaseStatus != null && (
                c.CaseStatus.ToLower().Contains("pend")  || c.CaseStatus.ToLower().Contains("admit") ||
                c.CaseStatus.ToLower().Contains("hear")  || c.CaseStatus.ToLower().Contains("stage") ||
                c.CaseStatus.ToLower().Contains("evid")  || c.CaseStatus.ToLower().Contains("argum") ||
                c.CaseStatus.ToLower().Contains("notic") || c.CaseStatus.ToLower().Contains("stay") ||
                c.CaseStatus.ToLower().Contains("trial") || c.CaseStatus.ToLower().Contains("appear")
            )) || (c.CaseStage != null && (
                c.CaseStage.ToLower().Contains("pend")  || c.CaseStage.ToLower().Contains("admit") ||
                c.CaseStage.ToLower().Contains("hear")  || c.CaseStage.ToLower().Contains("stage") ||
                c.CaseStage.ToLower().Contains("evid")  || c.CaseStage.ToLower().Contains("argum") ||
                c.CaseStage.ToLower().Contains("notic") || c.CaseStage.ToLower().Contains("stay") ||
                c.CaseStage.ToLower().Contains("trial") || c.CaseStage.ToLower().Contains("appear")
            ))
        );

    public static readonly Func<LitigationCase, bool> IsDisposedCompiled = CompileInvariant(IsDisposedExpr);
    public static readonly Func<LitigationCase, bool> IsPendingCompiled = CompileInvariant(IsPendingExpr);
    public static readonly Func<LitigationCase, bool> IsUnknownCompiled = CompileInvariant(IsUnknownExpr);

    public static LitigationCaseStatusBucket Classify(LitigationCase? c)
    {
        if (c is null) return LitigationCaseStatusBucket.Unknown;
        if (IsDisposedCompiled(c)) return LitigationCaseStatusBucket.Disposed;
        if (IsPendingCompiled(c)) return LitigationCaseStatusBucket.Pending;
        return LitigationCaseStatusBucket.Unknown;
    }

    public static LitigationCaseStatusBucket Classify(string? caseStatus, string? caseStage = null) =>
        Classify(new LitigationCase { CaseStatus = caseStatus, CaseStage = caseStage });

    public static bool IsConcluded(string? status, string? stage) =>
        Classify(status, stage) == LitigationCaseStatusBucket.Disposed;
}
```

Both in-memory evaluation (for JSON-backed `LegalCaseRecord` items via `CompileInvariant` / `Classify`) and database queries (for `LitigationCase` entities via LINQ-to-Entities) share this exact set of terminal terms:
- **Terminal judicial terms**: `dispos` ("Disposed", "Disposed of", "Disposed off"), `clos` ("Closed"), `dismis` ("Dismissed"), `withdr` ("Withdrawn"), `settl` ("Settled"), `decid` ("Decided"), `quash` ("Quashed"), `decree` ("Decreed"), `allow` ("Allowed"), `reject` ("Rejected"), `transfer` ("Transferred"), `compromis` ("Compromised").
- **Mutual Exclusivity Guaranteed**: For every possible case, exactly one of `IsDisposedExpr`, `IsPendingExpr`, or `IsUnknownExpr` evaluates to `true` in both SQL and C# memory. A case with `CaseStatus="Transferred", CaseStage="Hearing"` matches `IsDisposedExpr=true`, `IsPendingExpr=false`, and `IsUnknownExpr=false`.

#### Multi-Faceted Filter Toolbar
- **Search Box**: Free text search filtering across CaseRowId, CNR Number, Case Number, Court Name, Act, Petitioner, Respondent.
- **Forum Category**: Dropdown: `All`, `High Court`, `District Court`, `DRT`, `NCLT`, `Consumer`, `Others`.
- **Direction**: Dropdown: `All`, `Against Borrower`, `By Borrower`.
- **Status / Stage**: Dropdown: `All`, `Pending`, `Disposed` (evaluating `IsConcluded(Status, CaseStage)`).
- **Inclusion Filter**: Dropdown: `All Cases`, `Included in Report Only`, `Excluded from Report Only`.

#### Case Inventory Table
- **Selection**: Checkbox column on every row.
- **Case No & CNR**: Stable `CaseRowId` badge + CNR Number (monospace) + Case Type/Number.
- **Court & Forum**: Court Name, Bench, Forum category badge.
- **Direction**: Direction badge (`Against` in red, `By` in blue).
- **Parties**: Petitioner vs. Respondent.
- **Act & Classification**: Applicable Act (e.g. `NI Act S.138`, `SARFAESI`, `IBC`), Case Stage.
- **Filing & Hearing Dates**: Date of filing, Last hearing date, Next hearing date.
- **Report Status**: Badge: `Included` (green) or `Excluded: [Reason]` (gray).
- **Row Action**: **"Edit" Button** opening the Single Case Edit Modal for that `CaseRowId`.

---

### 11.4 Single Case Edit Modal

Opened upon clicking "Edit" on any specific case row:

- **Case Identification (Read-Only)**: `CaseRowId`, CNR Number, Case Number, Court / Forum.
- **Concluded / Terminal Case Immutability Rule**: Evaluated via `LitigationCaseStatusClassifier.IsConcluded(caseRecord.CaseStatus, caseRecord.CaseStage)`. If either field matches the terminal set (`Disposed`, `Disposed of/off`, `Dismissed`, `Transferred`, `Compromised`, `Decreed`, `Settled`, `Withdrawn`, `Closed`, `Quashed`, `Rejected`, `Allowed`), judicial status and stage are **strictly locked / immutable**. Analysts cannot alter the case's judicial status or stage. A warning badge is displayed: `[Status / Stage] (Concluded Matter — Status & Stage Locked)`.
- **Classification & Stage (Editable ONLY for Non-Concluded Cases)**: Dropdowns to override Case Classification, Stage, and Status (e.g., Pending, Stayed). Disabled if `IsConcluded(Status, CaseStage) == true`.
- **Report Inclusion Switch**: Toggle control: `Include in SBI Report` (ON / OFF). Remains available even for concluded cases so analysts can choose whether to disclose concluded matters in the credit report.
- **Exclusion Reason (Mandatory if Excluded)**:
  - Predefined options:
    - `"Matter Settled / Decreed / Disposed"`
    - `"Case Dismissed / Withdrawn"`
    - `"Transferred to Another Court / Tribunal"`
    - `"Matter Compromised Between Parties"`
    - `"Claim Value Below Materiality Threshold (< ₹5 Lakhs)"`
    - `"Interim / Procedural Matter Only"`
    - `"Duplicate Filing / Transferred CNR"`
    - `"Name-Match Only (Unrelated Third Party)"`
    - `"Other (Specify Note)"`
- **Analyst Note**: Free text field for audit rationale.
- **Save Action**: Submits `CaseRowId` and `submittedDataVersion`. Executes conditional atomic write into `job.DataJson` (§11.5), updates bucket counts, and marks report draft for instant regeneration.

---

### 11.5 High-Volume Batch Case Updates (100s of Cases)

For large corporate borrowers with 100s or 1,000s of cases (e.g. infrastructure, real estate, retail borrowers), editing cases one-by-one is prohibitive. The system provides a high-volume batch processing engine:

#### 1. Dual-Scope Selection Controls
- Master checkbox in table header with dropdown:
  - **"Select Visible (e.g. 25)"**: Selects all cases currently rendered on the page.
  - **"Select All Matching Filter (e.g. 142)"**: Selects all cases matching the active filter criteria across all pagination pages.

#### 2. Floating Batch Action Toolbar
Appears automatically when ≥ 1 cases are selected:
- Selected counter: e.g. `142 cases selected`.
- **"Bulk Exclude from Report"**:
  - Opens modal: analyst selects exclusion reason (e.g. `"Claim Value Below Materiality Threshold"` or `"Case Dismissed / Disposed"`) and optional notes.
  - Marks all selected cases as excluded in one transaction.
- **"Bulk Include in Report"**:
  - Restores inclusion for all selected cases.
- **"Bulk Update Status / Stage"**:
  - Sets Case Status (e.g. `Stayed`, `Pending`) across selected rows.
  - **Concluded Case Protection Rule**: Any case satisfying `LitigationCaseStatusClassifier.IsConcluded(c.CaseStatus, c.CaseStage) == true` (matching `Disposed`, `Disposed of/off`, `Dismissed`, `Transferred`, `Compromised`, `Decreed`, `Settled`, `Closed`, `Quashed`, `Allowed`, `Rejected` in **either status or stage**) is **strictly protected and skipped from status/stage mutations**. Concluded judicial records cannot be modified in bulk.
- **"Deselect All"**: Clears selection.

#### 3. Atomic Batch Update API Contract & Concurrency Guard
- **Endpoint**: `POST /Assignments/BatchUpdateCases/{batchId}/{id}`
- **Payload**:
  ```json
  {
    "CaseRowIds": ["C-001", "C-002", "C-003"],
    "SelectAllMatchingFilter": false,
    "Filter": { "Category": "district", "Direction": "against" },
    "Operation": "Exclude" | "Include" | "UpdateStatus",
    "ExclusionReason": "Claim Value Below Materiality Threshold (< ₹5 Lakhs)",
    "NewStatus": "Pending",
    "AnalystNote": "Bulk reviewed against bank materiality guidelines.",
    "SubmittedDataVersion": 3
  }
  ```
- **Server-Side Execution & Write-Time Concurrency Check**:
  ```csharp
  await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

  // 1. Load job with tenant batch isolation
  var job = await db.PreLoginReportJobs
      .FirstOrDefaultAsync(j => j.PreLoginReportJobId == id && j.BatchId == batchId, ct);
  if (job is null) throw new PreLoginReportNotFoundException();

  if (job.DataVersion != payload.SubmittedDataVersion)
  {
      await tx.RollbackAsync(ct);
      throw new PreLoginConcurrencyException("Assignment cases were modified by another user. Please refresh.");
  }

  // 2. Deserialize report data
  var reportData = JsonSerializer.Deserialize<InstaReportData>(job.DataJson!)!;
  var targetedCases = ResolveTargetedCases(reportData.LegalCases.Cases, payload);

  int updatedCount = 0;
  int skippedConcludedCount = 0;

  foreach (var c in targetedCases)
  {
      if (payload.Operation == "UpdateStatus")
      {
          // Strict dual status/stage guard:
          if (LitigationCaseStatusClassifier.IsConcluded(c.CaseStatus, c.CaseStage))
          {
              skippedConcludedCount++;
              continue;
          }
          c.CaseStatus = payload.NewStatus;
          updatedCount++;
      }
      else if (payload.Operation == "Exclude")
      {
          c.IsExcludedFromReport = true;
          c.ExclusionReason = payload.ExclusionReason;
          c.AnalystNote = payload.AnalystNote;
          updatedCount++;
      }
      else if (payload.Operation == "Include")
      {
          c.IsExcludedFromReport = false;
          c.ExclusionReason = null;
          updatedCount++;
      }
  }

  // 3. Re-bucket summary statistics
  reportData.LegalCases.RecalculateSummaryMetrics();
  var updatedJson = JsonSerializer.Serialize(reportData);

  // 4. Atomic conditional write enforcing DataVersion increment:
  var affected = await db.PreLoginReportJobs
      .Where(j => j.PreLoginReportJobId == id
          && j.BatchId == batchId
          && j.DataVersion == payload.SubmittedDataVersion)
      .ExecuteUpdateAsync(s => s
          .SetProperty(j => j.DataJson, updatedJson)
          .SetProperty(j => j.DataVersion, j => j.DataVersion + 1), ct);

  if (affected == 0)
  {
      await tx.RollbackAsync(ct);
      throw new PreLoginConcurrencyException("Concurrent update detected at write time. Changes rolled back.");
  }

  await tx.CommitAsync(ct);

  return new BatchUpdateResult
  {
      UpdatedCount = updatedCount,
      SkippedConcludedCount = skippedConcludedCount,
      NewDataVersion = payload.SubmittedDataVersion + 1,
      Message = $"Updated {updatedCount} cases; {skippedConcludedCount} concluded cases (disposed/dismissed/transferred/compromised) preserved without modification."
  };
  ```

---

### 11.6 Concluded Case Ingestion & Refresh Rule
During background litigation ingestion or future refresh sweeps, court cases that have reached a final judicial disposition (`Disposed`, `Disposed of/off`, `Dismissed`, `Transferred`, `Compromised`, `Decreed`, `Settled`, `Closed`, `Quashed`, `Allowed`, `Rejected` per `LitigationCaseStatusClassifier.IsConcluded`) are treated as terminal/immutable court records: subsequent polls or reconciliations do not overwrite their concluded status or stage with in-progress stages.


