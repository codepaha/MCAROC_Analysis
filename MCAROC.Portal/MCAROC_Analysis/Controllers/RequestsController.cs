using System.Security.Cryptography;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Controllers;

public class RequestsController(
    AppDbContext db,
    IngestionOrchestrator orchestrator,
    FileValidationService fileValidation,
    IWebHostEnvironment env) : Controller
{
    [HttpGet]
    public async Task<IActionResult> New()
    {
        var clients = await db.Clients.Where(c => c.IsActive).OrderBy(c => c.ClientName).ToListAsync();
        return View(new NewRequestViewModel { Clients = clients });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
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
            vm.FinancialYears = await db.FinancialYearData.Where(x => x.IngestionRunId == runId)
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

        return View(vm);
    }

    private async Task<RequestDocument> SaveDocumentAsync(long requestId, IFormFile file, DocumentType documentType)
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

        var openCheck = fileValidation.ValidateOpens(fullPath);
        document.StoredFileName = storedFileName;
        document.StoragePath = fullPath;
        if (!openCheck.IsValid)
        {
            document.UploadStatus = DocumentUploadStatus.ValidationFailed;
            document.QuarantineReason = openCheck.Error;
        }

        await db.SaveChangesAsync();
        return document;
    }
}
