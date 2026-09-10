using System.Security.Cryptography;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Dashboard;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Controllers;

public class RequestsController(
    AppDbContext db,
    IngestionOrchestrator orchestrator,
    FileValidationService fileValidation,
    AnalysisQueue analysisQueue,
    FilingProcessingQueue filingQueue,
    RequestListQueryService requestListQueryService,
    IWebHostEnvironment env) : Controller
{
    [HttpGet("/Requests")]
    public async Task<IActionResult> Index([FromQuery] RequestListFilterCriteria filters)
    {
        var vm = await requestListQueryService.SearchAsync(filters);
        return View(vm);
    }

    [HttpGet]
    public async Task<IActionResult> New()
    {
        var clients = await db.Clients.Where(c => c.IsActive).OrderBy(c => c.ClientName).ToListAsync();
        return View(new NewRequestViewModel { Clients = clients });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(2_000_000_000)] // MCA Filings archives run up to ~700MB in the real sample corpus
    [RequestFormLimits(MultipartBodyLengthLimit = 2_000_000_000)]
    public async Task<IActionResult> New(NewRequestViewModel model)
    {
        model.Clients = await db.Clients.Where(c => c.IsActive).OrderBy(c => c.ClientName).ToListAsync();

        if (string.IsNullOrWhiteSpace(model.Cin) && string.IsNullOrWhiteSpace(model.Pan))
        {
            model.ErrorMessage = "At least one of CIN/LLPIN or PAN is required.";
            return View(model);
        }

        if (model.RocFile is null || model.RocFile.Length == 0)
        {
            model.ErrorMessage = "The MCA / ROC Report file is required.";
            return View(model);
        }

        await using (var rocStream = model.RocFile.OpenReadStream())
        {
            var rocCheck = fileValidation.ValidateUpload(model.RocFile.FileName, model.RocFile.Length, rocStream);
            if (!rocCheck.IsValid)
            {
                model.ErrorMessage = $"MCA / ROC Report: {rocCheck.Error}";
                return View(model);
            }
        }

        if (model.ChargeFile is { Length: > 0 })
        {
            await using var chargeStream = model.ChargeFile.OpenReadStream();
            var chargeCheck = fileValidation.ValidateUpload(model.ChargeFile.FileName, model.ChargeFile.Length, chargeStream);
            if (!chargeCheck.IsValid)
            {
                model.ErrorMessage = $"Detailed Charge Report: {chargeCheck.Error}";
                return View(model);
            }
        }

        var request = new McaRequest
        {
            ClientId = model.ClientId,
            EntityType = model.EntityType,
            CompanyName = model.CompanyName,
            Cin = string.IsNullOrWhiteSpace(model.Cin) ? null : model.Cin.Trim().ToUpperInvariant(),
            Pan = string.IsNullOrWhiteSpace(model.Pan) ? null : model.Pan.Trim().ToUpperInvariant(),
            Llpin = model.EntityType == EntityType.LLP ? model.Cin?.Trim().ToUpperInvariant() : null,
            RequestStatus = RequestStatus.Created,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        request.RequestNumber = $"MCA-{request.CreatedDate:yyyyMMdd}-{request.RequestId:D6}";
        await db.SaveChangesAsync();

        var rocDocument = await SaveDocumentAsync(request.RequestId, model.RocFile, DocumentType.McaRocReport);
        long? chargeDocumentId = null;
        if (model.ChargeFile is { Length: > 0 })
        {
            var chargeDocument = await SaveDocumentAsync(request.RequestId, model.ChargeFile, DocumentType.ChargeReport);
            chargeDocumentId = chargeDocument.DocumentId;
        }

        request.RequestStatus = RequestStatus.DocumentsUploaded;
        request.AnalysisStartedDate = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await orchestrator.RunAsync(request.RequestId, rocDocument.DocumentId, chargeDocumentId);

        // Auto-trigger analysis only when ingestion completed cleanly enough to trust — IsManualReviewRequired
        // is set only by IngestionOrchestrator's identity-mismatch checks (form-vs-ROC, ROC-vs-charge), never
        // by an optional missing sheet, so this one check is exactly the block/don't-block line: a missing
        // GST/EPFO/Litigation sheet still reaches DataExtracted normally and should still be analyzed.
        if (request.RequestStatus == RequestStatus.DataExtracted && !request.IsManualReviewRequired)
            analysisQueue.Enqueue(request.RequestId);

        if (model.McaFilingsFile is { Length: > 0 } && Path.GetExtension(model.McaFilingsFile.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var filingsDocument = await SaveDocumentAsync(request.RequestId, model.McaFilingsFile, DocumentType.McaFilingsArchive, validateAsExcel: false);
            var batch = new McaFilingBatch
            {
                RequestId = request.RequestId,
                SourceDocumentId = filingsDocument.DocumentId,
                Status = FilingBatchStatus.Uploaded,
                StartedDate = DateTime.UtcNow
            };
            db.McaFilingBatches.Add(batch);
            await db.SaveChangesAsync();
            filingQueue.Enqueue(new UnpackBatchWorkItem(batch.BatchId));
        }

        return RedirectToAction(nameof(Details), new { id = request.RequestId });
    }

    // ── Dev-only re-ingest ────────────────────────────────────────────────────
    //
    // The normal flow only creates brand-new requests. When the parsers improve, or a request was
    // ingested from an incomplete workbook export, there is no way to re-run ingestion against a
    // fuller/newer file set on the SAME request (keeping its already-processed MCA-filings archive,
    // documents and embeddings). This pair of actions does exactly that — a new IngestionRun N+1
    // (IngestionOrchestrator never deletes a prior run) plus an auto-enqueued analysis pass.
    // Development-only: it is a maintenance aid, not a product surface.

    [HttpGet("/Requests/{id:long}/reingest")]
    public async Task<IActionResult> Reingest(long id)
    {
        if (!env.IsDevelopment()) return NotFound();
        var request = await db.Requests.FirstOrDefaultAsync(r => r.RequestId == id);
        if (request is null) return NotFound();
        return View(request);
    }

    [HttpPost("/Requests/{id:long}/reingest")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(2_000_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 2_000_000_000)]
    public async Task<IActionResult> Reingest(long id, IFormFile? rocFile, IFormFile? chargeFile)
    {
        if (!env.IsDevelopment()) return NotFound();

        var request = await db.Requests.FirstOrDefaultAsync(r => r.RequestId == id);
        if (request is null) return NotFound();

        if (rocFile is null || rocFile.Length == 0)
        {
            TempData["ReingestError"] = "The MCA / ROC Report file is required.";
            return RedirectToAction(nameof(Reingest), new { id });
        }

        await using (var rocStream = rocFile.OpenReadStream())
        {
            var rocCheck = fileValidation.ValidateUpload(rocFile.FileName, rocFile.Length, rocStream);
            if (!rocCheck.IsValid)
            {
                TempData["ReingestError"] = $"MCA / ROC Report: {rocCheck.Error}";
                return RedirectToAction(nameof(Reingest), new { id });
            }
        }

        if (chargeFile is { Length: > 0 })
        {
            await using var chargeStream = chargeFile.OpenReadStream();
            var chargeCheck = fileValidation.ValidateUpload(chargeFile.FileName, chargeFile.Length, chargeStream);
            if (!chargeCheck.IsValid)
            {
                TempData["ReingestError"] = $"Detailed Charge Report: {chargeCheck.Error}";
                return RedirectToAction(nameof(Reingest), new { id });
            }
        }

        var rocDocument = await SaveDocumentAsync(id, rocFile, DocumentType.McaRocReport);
        long? chargeDocumentId = chargeFile is { Length: > 0 }
            ? (await SaveDocumentAsync(id, chargeFile, DocumentType.ChargeReport)).DocumentId
            : null;

        // Fresh attempt: clear a stale manual-review flag from a prior run so a clean re-ingest can
        // auto-enqueue analysis (the orchestrator re-sets it if this run's identity checks fail).
        request.IsManualReviewRequired = false;
        request.ManualReviewReason = null;
        request.RequestStatus = RequestStatus.DocumentsUploaded;
        request.AnalysisStartedDate = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await orchestrator.RunAsync(id, rocDocument.DocumentId, chargeDocumentId);

        if (request.RequestStatus == RequestStatus.DataExtracted && !request.IsManualReviewRequired)
            analysisQueue.Enqueue(id);

        TempData["ReingestOk"] = request.RequestStatus == RequestStatus.DataExtracted
            ? "Re-ingestion complete — analysis has been queued."
            : $"Re-ingestion finished with status {request.RequestStatus}. {request.FailureReason ?? request.ManualReviewReason}";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet("/Requests/{id:long}")]
    public async Task<IActionResult> Details(long id, [FromQuery] long? charge)
    {
        var request = await db.Requests.Include(r => r.Client).FirstOrDefaultAsync(r => r.RequestId == id);
        if (request is null) return NotFound();

        var documents = await db.RequestDocuments.Where(d => d.RequestId == id).ToListAsync();
        var vm = new RequestDetailsViewModel { Request = request, Documents = documents, FocusChargeId = charge };

        if (request.LatestCompletedIngestionRunId is { } runId)
        {
            vm.LatestRun = await db.IngestionRuns.FirstOrDefaultAsync(r => r.IngestionRunId == runId);
            vm.Issues = await db.IngestionIssues.Where(i => i.IngestionRunId == runId).ToListAsync();

            vm.CompanyProfile = await db.CompanyProfiles.FirstOrDefaultAsync(x => x.IngestionRunId == runId);
            vm.CompanyEmails = await db.CompanyEmails.Where(x => x.IngestionRunId == runId).ToListAsync();
            vm.Directors = await db.Directors.Where(x => x.IngestionRunId == runId).OrderBy(x => x.NameRaw).ToListAsync();
            vm.DirectorAssociations = await db.DirectorAssociations.Where(x => x.IngestionRunId == runId).ToListAsync();
            vm.Shareholdings = await db.Shareholdings.Where(x => x.IngestionRunId == runId)
                .OrderByDescending(x => x.FinancialYear).ToListAsync();
            vm.FinancialYears = await db.FinancialYearData
                .Where(x => x.IngestionRunId == runId && x.Basis == FinancialBasis.Standalone)
                .OrderBy(x => x.FinancialYear).ToListAsync();
            vm.ConsolidatedFinancialYears = await db.FinancialYearData
                .Where(x => x.IngestionRunId == runId && x.Basis == FinancialBasis.Consolidated)
                .OrderBy(x => x.FinancialYear).ToListAsync();
            vm.Charges = await db.RocCharges
                .Include(c => c.Events).ThenInclude(e => e.SecurityComponents)
                .Where(x => x.IngestionRunId == runId).ToListAsync();
            vm.MsmePayments = await db.MsmePayments.Where(x => x.IngestionRunId == runId).ToListAsync();
            vm.GstRegistrations = await db.GstRegistrations.Include(g => g.Filings)
                .Where(x => x.IngestionRunId == runId).ToListAsync();
            vm.EpfoContributions = await db.EpfoContributions.Where(x => x.IngestionRunId == runId)
                .OrderByDescending(x => x.WageMonth).ToListAsync();
            vm.AuditorObservations = await db.AuditorObservations.Where(x => x.IngestionRunId == runId)
                .OrderByDescending(x => x.FinancialYear).ToListAsync();
            vm.Litigations = await db.Litigations.Where(x => x.IngestionRunId == runId).ToListAsync();

            // Phase 6 — the 12 additional workbook sheets.
            vm.Structure = await db.CompanyStructures.FirstOrDefaultAsync(x => x.IngestionRunId == runId);
            vm.RelatedCorporates = await db.RelatedCorporates.Where(x => x.IngestionRunId == runId)
                .OrderBy(x => x.RelationshipType).ThenBy(x => x.EntityNameNormalized).ToListAsync();
            vm.ComplianceRecords = await db.ComplianceRecords.Where(x => x.IngestionRunId == runId)
                .OrderBy(x => x.RecordType).ThenByDescending(x => x.RecordDate).ToListAsync();
            vm.FinancialParameters = await db.FinancialParameters.Where(x => x.IngestionRunId == runId)
                .OrderBy(x => x.ParameterName).ThenByDescending(x => x.FinancialYear).ToListAsync();
            vm.SecurityAllotments = await db.SecurityAllotments.Where(x => x.IngestionRunId == runId)
                .OrderByDescending(x => x.AllotmentDate).ToListAsync();
            vm.ProprietorshipAssociations = await db.ProprietorshipAssociations.Where(x => x.IngestionRunId == runId)
                .OrderBy(x => x.DirectorNameRaw).ToListAsync();
            vm.DirectorAssignmentHistories = await db.DirectorAssignmentHistories.Where(x => x.IngestionRunId == runId)
                .OrderBy(x => x.DirectorNameRaw).ThenByDescending(x => x.AppointmentDate).ToListAsync();
            vm.PeerComparisonMetrics = await db.PeerComparisonMetrics.Where(x => x.IngestionRunId == runId)
                .OrderBy(x => x.MetricName).ThenByDescending(x => x.FinancialYear).ToListAsync();

            // Phase 7.0 completeness layer
            vm.CompanyOfficers = await db.CompanyOfficers.Where(x => x.IngestionRunId == runId)
                .OrderBy(x => x.NameRaw).ToListAsync();
            vm.FinancialFacts = await db.FinancialFacts.Where(x => x.IngestionRunId == runId)
                .OrderBy(x => x.Section).ThenBy(x => x.Label).ThenByDescending(x => x.FinancialYear).ToListAsync();
        }

        // Only the analysis computed FROM the ingestion run this page is rendering. After a re-ingest
        // (see Reingest), an older analysis belongs to the previous ingestion run and must NOT be shown
        // against the new data — the AI tab falls back to its "analysis in progress / not started"
        // state until the matching new analysis exists. AnalysisRun.IngestionRunId is the lineage.
        vm.LatestAnalysisRun = request.LatestCompletedIngestionRunId is { } analysisIngestionRunId
            ? await db.AnalysisRuns
                .Where(a => a.RequestId == id && a.IngestionRunId == analysisIngestionRunId)
                .OrderByDescending(a => a.RunNumber)
                .FirstOrDefaultAsync()
            : null;
        if (vm.LatestAnalysisRun is not null)
        {
            // Severity/TemporalStatus are stored as strings (HasConversion<string>), so ordering by them
            // in SQL would sort alphabetically, not by enum severity — fetch then sort in memory instead.
            var findings = await db.AnalysisFindings.Where(f => f.AnalysisRunId == vm.LatestAnalysisRun.AnalysisRunId).ToListAsync();
            vm.AnalysisFindings = findings
                .OrderByDescending(f => f.Severity).ThenByDescending(f => f.DisplayPriority).ThenByDescending(f => f.ObservationDate)
                .ToList();

            if (vm.LatestAnalysisRun.ExecutiveSummaryJson is { } summaryJson)
            {
                try { vm.ExecutiveSummary = System.Text.Json.JsonSerializer.Deserialize<ExecutiveSummary>(summaryJson); }
                catch (System.Text.Json.JsonException) { /* leave null — view shows findings without a summary */ }
            }
        }

        vm.FilingBatch = await db.McaFilingBatches.Where(b => b.RequestId == id)
            .OrderByDescending(b => b.StartedDate).FirstOrDefaultAsync();
        if (vm.FilingBatch is { } batch)
        {
            var filings = await db.McaFilings.Include(f => f.Documents)
                .Where(f => f.BatchId == batch.BatchId).ToListAsync();
            var extractions = await db.McaFilingExtractions
                .Where(e => e.FilingId != null && filings.Select(f => f.FilingId).Contains(e.FilingId!.Value))
                .ToListAsync();
            var extractionsByFiling = extractions.Where(e => e.FilingId.HasValue).ToDictionary(e => e.FilingId!.Value);

            foreach (var filing in filings)
            {
                var dominant = filing.Documents
                    .Where(d => d.Category != FilingCategory.Unclassified)
                    .GroupBy(d => d.Category)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key)
                    .FirstOrDefault();

                vm.FilingSummaries.Add(new FilingSummaryViewModel
                {
                    Filing = filing,
                    DominantCategory = dominant,
                    Extraction = extractionsByFiling.GetValueOrDefault(filing.FilingId)
                });

                foreach (var doc in filing.Documents.Where(d => d.DuplicateOfDocumentId == null))
                {
                    vm.FilingCategoryCounts[doc.Category] = vm.FilingCategoryCounts.GetValueOrDefault(doc.Category) + 1;
                    vm.TextExtractionMethodCounts[doc.TextExtractionMethod] = vm.TextExtractionMethodCounts.GetValueOrDefault(doc.TextExtractionMethod) + 1;
                    if (doc.ManualReviewRequired) vm.ManualReviewFilingCount++;
                }
            }

            vm.AiSuccessCount = extractions.Count(e => e.Status == ExtractionStatus.Success);
            vm.AiFailedCount = extractions.Count(e => e.Status == ExtractionStatus.Failed);

            vm.ChunkableDocumentCount = await db.McaFilingDocuments.CountAsync(d =>
                d.BatchId == batch.BatchId && d.DuplicateOfDocumentId == null
                && d.ProcessingStatus == FilingDocumentProcessingStatus.Completed);
            vm.ChunkedDocumentCount = await db.McaFilingDocuments.CountAsync(d =>
                d.BatchId == batch.BatchId && d.DuplicateOfDocumentId == null
                && d.ProcessingStatus == FilingDocumentProcessingStatus.Completed
                && d.ChunkingStatus == ChunkingStatus.Chunked);
        }

        var chatSession = await db.ChatSessions.FirstOrDefaultAsync(s => s.RequestId == id);
        if (chatSession is not null)
        {
            vm.ChatMessages = await db.ChatMessages
                .Where(m => m.ChatSessionId == chatSession.ChatSessionId)
                .OrderBy(m => m.CreatedDate)
                .ToListAsync();
        }

        return View(vm);
    }

    /// <summary>Paged filing/document list for the Documents tab — returns a server-rendered partial so the
    /// tab body itself stays lightweight (counts only) on the main Details load. <c>category</c> + <c>page</c>
    /// round-trip in the query string, so a given page is bookmarkable / shareable.</summary>
    [HttpGet("/Requests/{id:long}/Documents")]
    public async Task<IActionResult> Documents(long id, [FromQuery] string? category, [FromQuery] int page = 1)
    {
        const int pageSize = 25;

        var request = await db.Requests.FirstOrDefaultAsync(r => r.RequestId == id);
        if (request is null) return NotFound();

        var vm = new DocumentsPageViewModel { RequestId = id, Category = category, PageSize = pageSize };

        var batch = await db.McaFilingBatches.Where(b => b.RequestId == id)
            .OrderByDescending(b => b.StartedDate).FirstOrDefaultAsync();
        if (batch is null)
            return PartialView("Details/_DocumentsList", vm);

        vm.HasBatch = true;

        // Dominant category per filing — derived from a lightweight (filing, category, count) grouping in
        // the database, not by loading every McaFilingDocument entity into memory.
        var catGroups = await db.McaFilingDocuments
            .Where(d => d.BatchId == batch.BatchId && d.DuplicateOfDocumentId == null && d.Category != FilingCategory.Unclassified)
            .GroupBy(d => new { d.FilingId, d.Category })
            .Select(g => new { g.Key.FilingId, g.Key.Category, Count = g.Count() })
            .ToListAsync();
        var dominantByFiling = catGroups
            .GroupBy(x => x.FilingId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Count).First().Category);

        var allFilings = await db.McaFilings.Where(f => f.BatchId == batch.BatchId)
            .Select(f => new { f.FilingId, f.Srn }).ToListAsync();

        FilingCategory DominantFor(long filingId) =>
            dominantByFiling.TryGetValue(filingId, out var c) ? c : FilingCategory.Unclassified;

        foreach (var f in allFilings)
            vm.CategoryCounts[DominantFor(f.FilingId)] = vm.CategoryCounts.GetValueOrDefault(DominantFor(f.FilingId)) + 1;

        FilingCategory? filterCat = !string.IsNullOrWhiteSpace(category)
            && Enum.TryParse<FilingCategory>(category, ignoreCase: true, out var parsed) ? parsed : null;

        var ordered = allFilings
            .Where(f => filterCat is null || DominantFor(f.FilingId) == filterCat)
            .OrderBy(f => DominantFor(f.FilingId))
            .ThenBy(f => f.Srn, StringComparer.Ordinal)
            .Select(f => f.FilingId)
            .ToList();

        vm.TotalFilings = ordered.Count;
        // Clamp the requested page into [1, TotalPages] *before* any arithmetic — an unbounded ?page=
        // (e.g. int.MaxValue) would otherwise overflow (vm.Page - 1) * pageSize.
        vm.Page = Math.Clamp(page, 1, Math.Max(1, vm.TotalPages));

        var pageIds = ordered.Skip((vm.Page - 1) * pageSize).Take(pageSize).ToList();

        var pageFilings = await db.McaFilings.Include(f => f.Documents)
            .Where(f => pageIds.Contains(f.FilingId)).ToListAsync();
        var extractionByFiling = (await db.McaFilingExtractions
                .Where(e => e.FilingId != null && pageIds.Contains(e.FilingId!.Value)).ToListAsync())
            .Where(e => e.FilingId.HasValue)
            .ToDictionary(e => e.FilingId!.Value);

        vm.Filings = pageIds // preserve the (category, SRN) page order
            .Select(fid => pageFilings.First(f => f.FilingId == fid))
            .Select(f => new DocumentsPageViewModel.FilingRow(f, DominantFor(f.FilingId), extractionByFiling.GetValueOrDefault(f.FilingId)))
            .ToList();

        return PartialView("Details/_DocumentsList", vm);
    }

    private async Task<RequestDocument> SaveDocumentAsync(long requestId, IFormFile file, DocumentType documentType, bool validateAsExcel = true)
    {
        var document = new RequestDocument
        {
            RequestId = requestId,
            DocumentType = documentType,
            OriginalFileName = file.FileName,
            FileSize = file.Length,
            UploadStatus = DocumentUploadStatus.Uploaded,
            UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(document);
        await db.SaveChangesAsync(); // need DocumentId for the internal filename

        var ext = Path.GetExtension(file.FileName);
        var storedFileName = $"{document.DocumentId}{ext}";
        var uploadsDir = Path.Combine(env.ContentRootPath, "App_Data", "Uploads", requestId.ToString(), "original");
        Directory.CreateDirectory(uploadsDir);
        var fullPath = Path.Combine(uploadsDir, storedFileName);

        await using (var fileStream = new FileStream(fullPath, FileMode.Create))
        {
            await file.CopyToAsync(fileStream);
        }

        using (var sha256 = SHA256.Create())
        await using (var hashStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read))
        {
            var hashBytes = await sha256.ComputeHashAsync(hashStream);
            document.FileHash = Convert.ToHexString(hashBytes);
        }

        document.StoredFileName = storedFileName;
        document.StoragePath = fullPath;
        if (validateAsExcel)
        {
            var openCheck = fileValidation.ValidateOpens(fullPath);
            if (!openCheck.IsValid)
            {
                document.UploadStatus = DocumentUploadStatus.ValidationFailed;
                document.QuarantineReason = openCheck.Error;
            }
        }

        await db.SaveChangesAsync();
        return document;
    }
}
