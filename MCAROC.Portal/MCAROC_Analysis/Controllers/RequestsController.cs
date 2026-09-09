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

    [HttpGet("/Requests/{id:long}")]
    public async Task<IActionResult> Details(long id)
    {
        var request = await db.Requests.Include(r => r.Client).FirstOrDefaultAsync(r => r.RequestId == id);
        if (request is null) return NotFound();

        var documents = await db.RequestDocuments.Where(d => d.RequestId == id).ToListAsync();
        var vm = new RequestDetailsViewModel { Request = request, Documents = documents };

        if (request.LatestCompletedIngestionRunId is { } runId)
        {
            vm.LatestRun = await db.IngestionRuns.FirstOrDefaultAsync(r => r.IngestionRunId == runId);
            vm.Issues = await db.IngestionIssues.Where(i => i.IngestionRunId == runId).ToListAsync();

            vm.CompanyProfile = await db.CompanyProfiles.FirstOrDefaultAsync(x => x.IngestionRunId == runId);
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
            vm.Charges = await db.RocCharges.Include(c => c.Events).Where(x => x.IngestionRunId == runId).ToListAsync();
            vm.MsmePayments = await db.MsmePayments.Where(x => x.IngestionRunId == runId).ToListAsync();
            vm.GstRegistrations = await db.GstRegistrations.Include(g => g.Filings)
                .Where(x => x.IngestionRunId == runId).ToListAsync();
            vm.EpfoContributions = await db.EpfoContributions.Where(x => x.IngestionRunId == runId)
                .OrderByDescending(x => x.WageMonth).ToListAsync();
            vm.AuditorObservations = await db.AuditorObservations.Where(x => x.IngestionRunId == runId)
                .OrderByDescending(x => x.FinancialYear).ToListAsync();
            vm.Litigations = await db.Litigations.Where(x => x.IngestionRunId == runId).ToListAsync();
        }

        vm.LatestAnalysisRun = await db.AnalysisRuns
            .Where(a => a.RequestId == id)
            .OrderByDescending(a => a.RunNumber)
            .FirstOrDefaultAsync();
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
