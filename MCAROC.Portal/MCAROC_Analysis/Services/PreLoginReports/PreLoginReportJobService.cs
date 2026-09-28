using System.Text.Json;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.PreLoginReports;

public sealed class PreLoginReportJobService(AppDbContext db, PreLoginReportQueue queue, PreLoginReportService reports, IWebHostEnvironment environment,
    BorrowerAssignmentIntake? intake = null, BorrowerAssignmentIdentityResolver? identityResolver = null)
{
    public async Task<Guid> QueueRequestAsync(IFormFile? requestFile, string? requestText, CancellationToken ct)
    {
        if (requestFile is not { Length: > 0 } && string.IsNullOrWhiteSpace(requestText))
            throw new PreLoginReportException("Upload a request document or paste the request text.");
        if (requestText?.Length > 100_000) throw new PreLoginReportException("Request text exceeds 100,000 characters.");
        var batch = Guid.NewGuid();
        string? storagePath = null;
        string? fileName = null;
        if (requestFile is { Length: > 0 })
        {
            var extension = Path.GetExtension(requestFile.FileName).ToLowerInvariant();
            if (requestFile.Length > BorrowerRequestDocumentReader.MaxBytes || extension is not (".pdf" or ".png" or ".jpg" or ".jpeg" or ".doc" or ".docx" or ".eml" or ".txt"))
                throw new PreLoginReportException("Upload PDF, screenshot, Word, .eml email, or .txt up to 10 MB.");
            var directory = Path.Combine(environment.ContentRootPath, "App_Data", "BorrowerAssignments", batch.ToString("N"));
            Directory.CreateDirectory(directory);
            storagePath = Path.Combine(directory, "request" + extension);
            fileName = Path.GetFileName(requestFile.FileName);
            try
            {
                await using var target = File.Create(storagePath);
                await requestFile.CopyToAsync(target, ct);
                if (target.Length > BorrowerRequestDocumentReader.MaxBytes) throw new PreLoginReportException("Upload a document up to 10 MB.");
            }
            catch { if (File.Exists(storagePath)) File.Delete(storagePath); throw; }
        }
        if (storagePath is null)
        {
            var directory = Path.Combine(environment.ContentRootPath, "App_Data", "BorrowerAssignments", batch.ToString("N"));
            Directory.CreateDirectory(directory);
            storagePath = Path.Combine(directory, "request.txt"); fileName = "pasted-request.txt";
            await File.WriteAllTextAsync(storagePath, requestText!, ct);
            requestText = null;
        }
        else if (!string.IsNullOrWhiteSpace(requestText))
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(storagePath)!, "supplemental-text.txt"), requestText, ct);
        var job = new PreLoginReportJob
        {
            BatchId = batch, Cin = "ASSIGN-" + batch.ToString("N")[..20], Format = nameof(PreLoginReportFormat.Sbi),
            DataJson = JsonSerializer.Serialize(new BorrowerIntakePayload(new(storagePath, fileName, requestText?.Trim()))),
            Status = PreLoginReportJobStatus.Queued, CreatedUtc = DateTime.UtcNow
        };
        db.PreLoginReportJobs.Add(job);
        try { await db.SaveChangesAsync(ct); }
        catch { if (storagePath is not null && File.Exists(storagePath)) File.Delete(storagePath); throw; }
        queue.Enqueue(job.PreLoginReportJobId);
        return batch;
    }

    public InstaReportData? AssignmentData(PreLoginReportJob job) => BorrowerAssignmentIntake.PendingSource(job.DataJson) is null
        ? DeserializeStoredData(job.DataJson) : null;

    public async Task CompleteAssignmentAsync(Guid batch, long id, string? borrowerName, string? entityType,
        string? mcaIdentifier, IFormFile? legalCasesFile, bool noCasesConfirmed, CancellationToken ct)
    {
        var job = await FindInBatchAsync(batch, id, ct) ?? throw new PreLoginReportException("Assignment not found.");
        if (job.Status != PreLoginReportJobStatus.AwaitingReview) throw new PreLoginReportException("This assignment is not awaiting details or results.");
        var stored = AssignmentData(job) ?? throw new PreLoginReportException("Assignment recognition has not completed.");
        var details = stored.Assignment ?? throw new PreLoginReportException("Assignment details are unavailable.");
        if (string.IsNullOrWhiteSpace(borrowerName) || borrowerName.Length > 250 || string.IsNullOrWhiteSpace(entityType) || entityType.Length > 100)
            throw new PreLoginReportException("A borrower name and entity type are required to complete this assignment.");
        details.CompanyDetails.CompanyName = borrowerName.Trim();
        details.CompanyDetails.EntityType = entityType.Trim();
        var recognized = BorrowerAssignmentIntake.CreateData(details, new(stored.SourceStoragePath, stored.SourceFileName, null), stored.ExtractionModel ?? "manual correction");
        var data = recognized.Data;
        if (data.Company.LitigationOnly)
        {
            if (legalCasesFile is { Length: > 0 })
            {
                if (legalCasesFile.Length > BorrowerRequestDocumentReader.MaxBytes || Path.GetExtension(legalCasesFile.FileName).ToLowerInvariant() is not (".csv" or ".xls" or ".xlsx"))
                    throw new PreLoginReportException("Use a litigation results .xlsx/.xls/.csv up to 10 MB.");
                await using var stream = legalCasesFile.OpenReadStream();
                data = data with { LegalCases = LegalCaseFileParser.ToInstaLegalCases(LegalCaseFileParser.Parse(stream, legalCasesFile.FileName)) };
            }
            else if (noCasesConfirmed) data = data with { LegalCases = new(0, 0, 0, 0, 0, 0, 0, 0, 0, []) };
            else throw new PreLoginReportException("Attach litigation results, or confirm that a completed litigation search returned no cases.");
            job.Cin = details.CompanyDetails.Pan ?? "ASSIGN-" + batch.ToString("N")[..20];
        }
        else
        {
            var identifier = ValidateCins([mcaIdentifier ?? details.CompanyDetails.Cin ?? ""])[0];
            var entity = BorrowerRequestParser.EntityType(details);
            if (entity == PreLoginReportEntityType.Llp && !Regex.IsMatch(identifier, @"^[A-Z]{3}-[0-9]{4}$"))
                throw new PreLoginReportException("Enter an LLPIN for an LLP assignment.");
            if (entity != PreLoginReportEntityType.Llp && !Regex.IsMatch(identifier, @"^[UL]"))
                throw new PreLoginReportException("Enter an identifier supported by the current MCA report-data service.");
            if (entity is PreLoginReportEntityType.Company or PreLoginReportEntityType.Llp)
            {
                if (entity == PreLoginReportEntityType.Company) details.CompanyDetails.Cin = identifier;
                var confirmed = await (identityResolver ?? throw new PreLoginReportException("MCA identity resolution is not configured."))
                    .ResolveAsync(details, entity == PreLoginReportEntityType.Llp ? identifier : null, ct);
                if (confirmed.Identifier != identifier)
                    throw new PreLoginReportException("The selected CIN / LLPIN and borrower name do not match a company in the MCA master database.");
                data = data with { IdentityResolution = confirmed };
            }
            job.Cin = identifier;
            if (entity == PreLoginReportEntityType.Llp)
                data = data with { McaIdentifier = identifier };
            else
                details.CompanyDetails.Cin = identifier;
        }
        job.DataJson = JsonSerializer.Serialize(data);
        job.SubmittedCompanyName = details.CompanyDetails.CompanyName;
        job.Status = PreLoginReportJobStatus.Queued; job.ProgressPercent = 0; job.FailureReason = null; job.AttemptCount = 0; job.NextAttemptUtc = null;
        var changed = await db.PreLoginReportJobs.Where(x => x.PreLoginReportJobId == id && x.BatchId == batch && x.Status == PreLoginReportJobStatus.AwaitingReview)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.DataJson, job.DataJson).SetProperty(x => x.Cin, job.Cin)
                .SetProperty(x => x.SubmittedCompanyName, job.SubmittedCompanyName).SetProperty(x => x.Status, PreLoginReportJobStatus.Queued)
                .SetProperty(x => x.ProgressPercent, 0).SetProperty(x => x.FailureReason, (string?)null)
                .SetProperty(x => x.AttemptCount, 0).SetProperty(x => x.NextAttemptUtc, (DateTime?)null), ct);
        db.Entry(job).State = EntityState.Detached;
        if (changed == 0) throw new PreLoginReportException("This assignment has already been submitted for processing.");
        queue.Enqueue(job.PreLoginReportJobId);
    }

    public async Task<Guid> QueueBatchAsync(IEnumerable<string> cins, PreLoginReportFormat format, CancellationToken cancellationToken)
    {
        var normalized = ValidateCins(cins.Take(25));
        var batch = Guid.NewGuid();
        foreach (var cin in normalized)
        {
            db.PreLoginReportJobs.Add(new PreLoginReportJob { BatchId = batch, Cin = cin, Format = format.ToString(), Status = PreLoginReportJobStatus.Queued, ProgressPercent = 0, CreatedUtc = DateTime.UtcNow });
        }
        await db.SaveChangesAsync(cancellationToken);
        foreach (var id in await db.PreLoginReportJobs.Where(x => x.BatchId == batch).Select(x => x.PreLoginReportJobId).ToListAsync(cancellationToken)) queue.Enqueue(id);
        return batch;
    }

    /// <summary>Queues a single CIN the same way as a 1-CIN batch — the single-CIN and batch entry points
    /// share one pipeline, so a single-CIN request also lands in History and never blocks the browser on a
    /// slow live MCA fetch.</summary>
    public async Task<Guid> QueueSingleAsync(string cin, string? submittedCompanyName, PreLoginReportFormat format, CancellationToken cancellationToken)
    {
        var normalized = ValidateCins([cin]);
        var batch = Guid.NewGuid();
        db.PreLoginReportJobs.Add(new PreLoginReportJob
        {
            BatchId = batch, Cin = normalized[0], SubmittedCompanyName = submittedCompanyName, Format = format.ToString(),
            Status = PreLoginReportJobStatus.Queued, ProgressPercent = 0, CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        var id = await db.PreLoginReportJobs.Where(x => x.BatchId == batch).Select(x => x.PreLoginReportJobId).SingleAsync(cancellationToken);
        queue.Enqueue(id);
        return batch;
    }

    /// <summary>Partnership reports are intentionally self-contained: their details and legal-case counts
    /// are supplied by the user and serialized before the worker runs, so this path cannot call the MCA API.</summary>
    public async Task<Guid> QueuePartnershipAsync(PreLoginReportViewModel model, CancellationToken cancellationToken)
    {
        var validation = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        if (!model.IsLitigationOnly || !System.ComponentModel.DataAnnotations.Validator.TryValidateObject(model,
            new System.ComponentModel.DataAnnotations.ValidationContext(model), validation, true))
            throw new PreLoginReportException("Enter valid litigation-only assignment details.");
        var registrationNumber = model.PartnershipRegistrationNumber!.Trim().ToUpperInvariant();
        var entityType = model.EntityType == PreLoginReportEntityType.Other ? model.OtherEntityType?.Trim() ?? "Other" : model.EntityType.ToString();
        var data = new InstaReportData(
            new InstaCompany(model.PartnershipName!.Trim(), "-", registrationNumber, entityType, "-", "-", "-", "-", "-", "-",
                model.PartnershipAddress!.Trim(), "-", "-", "-", "-", "-", IsPartnership: model.EntityType == PreLoginReportEntityType.Partnership,
                IsLitigationOnly: true, EntityType: entityType),
            [], [], ToLegalCases(model.LegalCases));
        var batch = Guid.NewGuid();
        db.PreLoginReportJobs.Add(new PreLoginReportJob
        {
            BatchId = batch, Cin = registrationNumber, SubmittedCompanyName = data.Company.Name, Format = PreLoginReportFormat.Sbi.ToString(),
            DataJson = JsonSerializer.Serialize(data), Status = PreLoginReportJobStatus.Queued, ProgressPercent = 0, CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        queue.Enqueue(await db.PreLoginReportJobs.Where(x => x.BatchId == batch).Select(x => x.PreLoginReportJobId).SingleAsync(cancellationToken));
        return batch;
    }

    private static IReadOnlyList<string> ValidateCins(IEnumerable<string> cins)
    {
        var normalized = cins.Select(x => x.Trim().ToUpperInvariant()).Where(x => x.Length > 0).Distinct().ToList();
        if (normalized.Count == 0) throw new PreLoginReportException("Enter at least one CIN.");
        if (normalized.Any(cin => !Regex.IsMatch(cin, "^(?:[LU][0-9]{5}[A-Z]{2}[0-9]{4}[A-Z]{3}[0-9]{6}|[A-Z]{3}-[0-9]{4})$")))
            throw new PreLoginReportException("Enter a valid company CIN or LLPIN.");
        return normalized;
    }

    /// <summary>Jobs belonging to one batch — the only "history" a caller can see. There is no login on
    /// this pipeline, so the <see cref="PreLoginReportJob.BatchId"/> Guid IS the access credential: it is
    /// unguessable (random, 122 bits) and never listed anywhere, unlike the sequential
    /// <see cref="PreLoginReportJob.PreLoginReportJobId"/>. See #47 — the previous unscoped "return every
    /// job in the system" version let any anonymous visitor browse every user's CINs and download/rerun
    /// their reports by guessing/incrementing the bare id.</summary>
    public async Task<IReadOnlyList<PreLoginReportJob>> HistoryAsync(Guid batch, CancellationToken cancellationToken) =>
        await db.PreLoginReportJobs.Where(x => x.BatchId == batch).OrderByDescending(x => x.CreatedUtc).ToListAsync(cancellationToken);

    /// <summary>Looks up a job by id alone — for the background worker only (<see cref="PreLoginReportWorker"/>
    /// via <see cref="ProcessAsync"/>), which has no batch context and is not attacker-controlled. Every
    /// externally-reachable lookup (download/edit/rerun) MUST go through <see cref="FindInBatchAsync"/>
    /// instead, or it reintroduces #47's ownership gap.</summary>
    public async Task<PreLoginReportJob?> FindAsync(long id, CancellationToken cancellationToken) =>
        await db.PreLoginReportJobs.FindAsync([id], cancellationToken);

    /// <summary>The only job lookup safe to expose to an anonymous caller: returns null — identically,
    /// whether <paramref name="id"/> doesn't exist at all or exists under a DIFFERENT batch — so a caller
    /// who knows one batch's Guid can never probe for the existence of another batch's job ids (#47).</summary>
    public async Task<PreLoginReportJob?> FindInBatchAsync(Guid batch, long id, CancellationToken cancellationToken) =>
        await db.PreLoginReportJobs.FirstOrDefaultAsync(x => x.PreLoginReportJobId == id && x.BatchId == batch, cancellationToken);

    /// <summary>Loads a completed job's fetched data (captured in DataJson right after fetch, before
    /// generation) as an editable draft — the basis for the optional "Edit" action on the History page.
    /// Restricted to Completed jobs: DataJson is already populated once fetch finishes (status Generating),
    /// so without this check an edit opened against an in-flight job could regenerate concurrently with the
    /// worker's own ProcessAsync run and race it for job.ReportStoragePath/DataJson/Status. Restricted to
    /// <paramref name="batch"/> per #47 — see <see cref="FindInBatchAsync"/>.</summary>
    public async Task<PreLoginReportDraftViewModel> GetEditableDraftAsync(Guid batch, long id, CancellationToken cancellationToken)
    {
        var job = await FindInBatchAsync(batch, id, cancellationToken) ?? throw new PreLoginReportException("Report request not found.");
        if (job.Status != PreLoginReportJobStatus.Completed)
            throw new PreLoginReportException("This report is still being generated. Wait for it to complete before editing.");
        if (string.IsNullOrWhiteSpace(job.DataJson))
            throw new PreLoginReportException("This report has no fetched data available to edit.");
        var data = JsonSerializer.Deserialize<InstaReportData>(job.DataJson)
            ?? throw new PreLoginReportException("Stored report data is corrupt.");
        return PreLoginReportService.ToDraft(job.PreLoginReportJobId, job.BatchId, job.Cin, Enum.Parse<PreLoginReportFormat>(job.Format), data);
    }

    /// <summary>Applies a user's edits (including any added/removed charge or director rows) and
    /// regenerates the stored report in place. Same Completed-only restriction as <see cref="GetEditableDraftAsync"/>
    /// — see that method's remarks for the race this closes. Restricted to <paramref name="batch"/> per #47.</summary>
    public async Task ApplyEditAndRegenerateAsync(Guid batch, long id, PreLoginReportDraftViewModel draft, CancellationToken cancellationToken, IFormFile? legalCasesFile = null)
    {
        var job = await FindInBatchAsync(batch, id, cancellationToken) ?? throw new PreLoginReportException("Report request not found.");
        if (job.Status != PreLoginReportJobStatus.Completed)
            throw new PreLoginReportException("This report is still being generated. Wait for it to complete before editing.");
        var format = Enum.Parse<PreLoginReportFormat>(job.Format);
        var data = PreLoginReportService.ApplyEdits(draft);
        // IsPartnership is a fixed identity fact set at job creation (#217/#221), never form-editable —
        // re-attach it from the stored job rather than trust the posted draft.
        var stored = string.IsNullOrWhiteSpace(job.DataJson) ? null : JsonSerializer.Deserialize<InstaReportData>(job.DataJson);
        data = data with { Company = data.Company with { IsPartnership = stored?.Company.IsPartnership ?? false,
            IsLitigationOnly = stored?.Company.IsLitigationOnly ?? false, EntityType = stored?.Company.EntityType },
            Assignment = stored?.Assignment, SourceFileName = stored?.SourceFileName, SourceStoragePath = stored?.SourceStoragePath,
            ExtractionModel = stored?.ExtractionModel, McaIdentifier = stored?.McaIdentifier,
            IdentityResolution = stored?.IdentityResolution };
        if (data.Company.LitigationOnly) data = data with { Charges = [], Directors = [] };

        if (legalCasesFile is { Length: > 0 })
        {
            await using var stream = legalCasesFile.OpenReadStream();
            data = data with { LegalCases = LegalCaseFileParser.ToInstaLegalCases(LegalCaseFileParser.Parse(stream, legalCasesFile.FileName)) };
        }
        else if (stored?.LegalCases?.Cases is not null)
        {
            // No new upload this edit. The draft's hidden count fields are stale passthrough once a file has
            // ever been parsed for this job (the Edit page never lets the user hand-edit those counts) — the
            // previously-parsed Cases list (and its derived counts) is the authoritative source, so restore
            // it wholesale rather than keep ApplyEdits' bare count-only reconstruction from those fields.
            data = data with { LegalCases = stored.LegalCases };
        }

        // A partnership has no immutable MCA identifier: its PAN/registration number is a manually editable
        // field, so both identity rows in the regenerated document must use the edited value. This must key
        // off Company.IsPartnership, not LegalCases — Company/LLP jobs can carry LegalCases too now (#221).
        var identifier = data.Company.LitigationOnly ? data.Company.RegistrationNumber : job.Cin;
        var generated = await reports.GenerateFromDataAsync(identifier, format, data, cancellationToken);
        if (!string.IsNullOrWhiteSpace(job.ReportStoragePath) && File.Exists(job.ReportStoragePath)) File.Delete(job.ReportStoragePath);
        job.ReportStoragePath = await StoreReportAsync(job.PreLoginReportJobId, generated, cancellationToken);
        job.DataJson = JsonSerializer.Serialize(data);
        job.Cin = identifier;
        job.Status = PreLoginReportJobStatus.Completed; job.ProgressPercent = 100; job.CompletedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> StoreReportAsync(long jobId, GeneratedReport generated, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(environment.ContentRootPath, "App_Data", "PreLoginReports");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{jobId}-{generated.FileName}");
        await File.WriteAllBytesAsync(path, generated.Bytes, cancellationToken);
        return path;
    }

    /// <summary>Restricted to <paramref name="batch"/> per #47 — see <see cref="FindInBatchAsync"/>.</summary>
    public async Task RerunAsync(Guid batch, long id, CancellationToken cancellationToken)
    {
        var job = await FindInBatchAsync(batch, id, cancellationToken) ?? throw new PreLoginReportException("Report request not found.");
        if (job.Status == PreLoginReportJobStatus.AwaitingReview) throw new PreLoginReportException("This assignment is waiting for details or litigation results; rerunning cannot supply them.");
        job.Status = PreLoginReportJobStatus.Queued; job.ProgressPercent = 0; job.AttemptCount = 0; job.FailureReason = null; job.NextAttemptUtc = null; job.ReportStoragePath = null; job.CompletedUtc = null;
        await db.SaveChangesAsync(cancellationToken); queue.Enqueue(job.PreLoginReportJobId);
    }

    public async Task ProcessAsync(long id, CancellationToken cancellationToken)
    {
        var job = await FindAsync(id, cancellationToken);
        if (job is null || job.Status != PreLoginReportJobStatus.Queued) return;
        if (job.NextAttemptUtc is { } next && next > DateTime.UtcNow) { Schedule(id, next - DateTime.UtcNow); return; }
        var now = DateTime.UtcNow;
        var claimed = await db.PreLoginReportJobs.Where(x => x.PreLoginReportJobId == id && x.Status == PreLoginReportJobStatus.Queued)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, PreLoginReportJobStatus.Fetching).SetProperty(x => x.ProgressPercent, 10)
                .SetProperty(x => x.StartedUtc, x => x.StartedUtc ?? now).SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1), cancellationToken);
        if (claimed == 0) return;
        await db.Entry(job).ReloadAsync(cancellationToken);
        try
        {
            var format = Enum.Parse<PreLoginReportFormat>(job.Format);
            var source = BorrowerAssignmentIntake.PendingSource(job.DataJson);
            if (source is not null)
            {
                var recognized = await (intake ?? throw new PreLoginReportException("Assignment recognition is not configured.")).RecognizeAsync(source, cancellationToken);
                var intakeData = recognized.Data;
                var assignment = intakeData.Assignment!;
                var entity = BorrowerRequestParser.EntityType(assignment);
                string? reviewReason = recognized.MissingDetails;
                if (!intakeData.Company.LitigationOnly && entity is PreLoginReportEntityType.Company or PreLoginReportEntityType.Llp)
                {
                    var resolution = await (identityResolver ?? throw new PreLoginReportException("MCA identity resolution is not configured.", true))
                        .ResolveAsync(assignment, intakeData.McaIdentifier, cancellationToken);
                    intakeData = intakeData with { IdentityResolution = resolution, McaIdentifier = resolution.Identifier ?? intakeData.McaIdentifier };
                    if (resolution.Identifier is { } confirmed)
                    {
                        if (entity == PreLoginReportEntityType.Company) assignment.CompanyDetails.Cin = confirmed;
                        reviewReason = null;
                    }
                    else reviewReason = $"MCA identity needs review ({resolution.ReasonCode}). Select the correct database match before requesting details.";
                }
                else if (entity == PreLoginReportEntityType.ForeignCompany)
                    reviewReason = "Foreign-company identity needs review: the current report-data service has no verified FCRN lookup.";
                job.DataJson = JsonSerializer.Serialize(intakeData);
                job.SubmittedCompanyName = assignment.CompanyDetails.CompanyName;
                job.Cin = intakeData.McaIdentifier ?? assignment.CompanyDetails.Cin ?? intakeData.Company.RegistrationNumber;
                if (job.Cin == "-") job.Cin = "ASSIGN-" + job.BatchId.ToString("N")[..20];
                if (reviewReason is not null || intakeData.Company.LitigationOnly)
                {
                    job.Status = PreLoginReportJobStatus.AwaitingReview; job.ProgressPercent = 100;
                    job.FailureReason = reviewReason ?? "Assignment created. Litigation-only scope; awaiting litigation results.";
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }
            }
            var stored = DeserializeStoredData(job.DataJson);
            var data = stored?.Company.LitigationOnly == true || stored?.LegalCases is not null ? stored! :
                (await reports.FetchDataAsync(job.Cin, job.SubmittedCompanyName, cancellationToken)) with
                { Assignment = stored?.Assignment, SourceFileName = stored?.SourceFileName, SourceStoragePath = stored?.SourceStoragePath,
                    ExtractionModel = stored?.ExtractionModel, McaIdentifier = stored?.McaIdentifier, IdentityResolution = stored?.IdentityResolution };
            job.DataJson = JsonSerializer.Serialize(data); job.Status = PreLoginReportJobStatus.Generating; job.ProgressPercent = 60;
            await db.SaveChangesAsync(cancellationToken);
            var generated = await reports.GenerateFromDataAsync(job.Cin, format, data, cancellationToken);
            job.ReportStoragePath = await StoreReportAsync(job.PreLoginReportJobId, generated, cancellationToken);
            job.Status = PreLoginReportJobStatus.Completed; job.ProgressPercent = 100; job.CompletedUtc = DateTime.UtcNow; job.FailureReason = null;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (PreLoginReportException ex) { await FailOrRetryAsync(job, ex.Message, ex.Retryable, cancellationToken); }
        catch (Exception) { await FailOrRetryAsync(job, "Unexpected report processing failure.", retryable: true, cancellationToken); }
    }

    private async Task FailOrRetryAsync(PreLoginReportJob job, string reason, bool retryable, CancellationToken cancellationToken)
    {
        job.FailureReason = reason;
        if (retryable && job.AttemptCount < 3)
        {
            job.Status = PreLoginReportJobStatus.Queued; job.ProgressPercent = 0; job.NextAttemptUtc = DateTime.UtcNow.AddSeconds(20 * job.AttemptCount);
            await db.SaveChangesAsync(cancellationToken);
            Schedule(job.PreLoginReportJobId, TimeSpan.FromSeconds(20 * job.AttemptCount));
            return;
        }
        job.Status = PreLoginReportJobStatus.Failed; job.ProgressPercent = 100; job.CompletedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private void Schedule(long jobId, TimeSpan delay) => _ = Task.Run(async () => { await Task.Delay(delay); queue.Enqueue(jobId); });

    private static InstaReportData? DeserializeStoredData(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson)) return null;
        var data = JsonSerializer.Deserialize<InstaReportData>(dataJson);
        return data;
    }

    private static InstaLegalCases ToLegalCases(EditableLegalCasesViewModel cases) => new(
        cases.SupremeCourt, cases.HighCourt, cases.DistrictCourt, cases.ConsumerForum, cases.ItatTax,
        cases.NcltNclat, cases.DrtDrat, cases.Rera, cases.NgtOthers);
}
