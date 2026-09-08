using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services;

/// <summary>Drives one ingestion attempt for a request: reads the uploaded workbook(s), validates company
/// identity at both checkpoints (form vs ROC report, ROC report vs charge report), runs every section
/// parser, and persists everything tagged to a new IngestionRun in one transaction. Never deletes a prior
/// run's data — re-running creates IngestionRun N+1 and MCA_Request.LatestCompletedIngestionRunId moves
/// forward only on success, which is what makes reprocessing idempotent (see portalplan/plan doc).</summary>
public class IngestionOrchestrator(AppDbContext db, IExcelSheetReader sheetReader, ILogger<IngestionOrchestrator> logger)
{
    public async Task<IngestionRun> RunAsync(long requestId, long rocDocumentId, long? chargeDocumentId, CancellationToken ct = default)
    {
        var request = await db.Requests.FirstOrDefaultAsync(r => r.RequestId == requestId, ct)
            ?? throw new InvalidOperationException($"Request {requestId} not found.");
        var rocDocument = await db.RequestDocuments.FirstAsync(d => d.DocumentId == rocDocumentId, ct);
        var chargeDocument = chargeDocumentId is null
            ? null
            : await db.RequestDocuments.FirstAsync(d => d.DocumentId == chargeDocumentId, ct);

        var runNumber = await db.IngestionRuns.CountAsync(r => r.RequestId == requestId, ct) + 1;
        var run = new IngestionRun
        {
            RequestId = requestId,
            RunNumber = runNumber,
            StartedDate = DateTime.UtcNow,
            Status = IngestionRunStatus.Running,
            SourceRocDocumentId = rocDocumentId,
            SourceChargeDocumentId = chargeDocumentId
        };
        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync(ct); // need run.IngestionRunId before tagging extracted rows

        var issues = new List<IngestionIssue>();
        var itemCount = 0;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var rocWorkbook = sheetReader.ReadWorkbook(rocDocument.StoragePath);
            IReadOnlyList<SheetData>? chargeWorkbook = chargeDocument is not null
                ? sheetReader.ReadWorkbook(chargeDocument.StoragePath)
                : null;

            var companySheet = SheetAliases.Find(rocWorkbook, SheetAliases.CompanyProfile)
                ?? throw new IngestionFailedException("Required sheet 'About the Company' was not found in the ROC report.");

            var companyResult = CompanyProfileParser.Parse(companySheet, requestId, run.IngestionRunId, rocDocumentId);
            Collect(companyResult, issues, ref itemCount);
            if (companyResult.Errors.Count > 0)
                throw new IngestionFailedException(companyResult.Errors[0].Message);

            var companyProfile = companyResult.Items[0];
            db.CompanyProfiles.Add(companyProfile);

            ValidateFormVsRoc(request, companyProfile);

            var chargeIdentityMatches = true;
            if (chargeWorkbook is not null)
                chargeIdentityMatches = ValidateRocVsCharge(rocWorkbook, chargeWorkbook, chargeDocument!, request);

            RunSectionParsers(rocWorkbook, chargeWorkbook, chargeIdentityMatches, requestId, run.IngestionRunId,
                rocDocumentId, chargeDocumentId, issues, ref itemCount);

            foreach (var issue in issues)
                issue.IngestionRunId = run.IngestionRunId;
            db.IngestionIssues.AddRange(issues);

            run.RowsExtracted = itemCount;
            run.WarningsCount = issues.Count(i => i.Severity == IssueSeverity.Warning);
            run.ErrorsCount = issues.Count(i => i.Severity == IssueSeverity.Error);
            run.CompletedDate = DateTime.UtcNow;
            run.Status = issues.Count > 0 ? IngestionRunStatus.CompletedWithWarnings : IngestionRunStatus.CompletedClean;

            request.LatestCompletedIngestionRunId = run.IngestionRunId;
            request.HasIngestionWarnings = run.WarningsCount > 0;
            request.RequestStatus = RequestStatus.DataExtracted;
            request.AnalysisCompletedDate = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);
            logger.LogError(ex, "Ingestion failed for request {RequestId}, run {RunNumber}", requestId, runNumber);

            // Everything else added during this attempt (CompanyProfile, Directors, ...) is still tracked
            // as "Added" even though the DB transaction rolled back. Clear the tracker so the next
            // SaveChanges only touches run/request, not a second, unwrapped insert of the rolled-back rows.
            db.ChangeTracker.Clear();

            run.Status = IngestionRunStatus.Failed;
            run.FailureReason = ex.Message;
            run.CompletedDate = DateTime.UtcNow;
            db.IngestionRuns.Update(run);

            request.RequestStatus = RequestStatus.ExtractionFailed;
            request.FailureReason = ex.Message;
            db.Requests.Update(request);

            await db.SaveChangesAsync(ct);
        }

        return run;
    }

    private static void ValidateFormVsRoc(McaRequest request, CompanyProfile companyProfile)
    {
        var reasons = new List<string>();
        if (!string.IsNullOrEmpty(request.Cin) && !string.IsNullOrEmpty(companyProfile.Cin) &&
            !string.Equals(request.Cin, companyProfile.Cin, StringComparison.OrdinalIgnoreCase))
            reasons.Add($"Request CIN '{request.Cin}' does not match ROC report CIN '{companyProfile.Cin}'.");

        if (!string.IsNullOrEmpty(request.Pan) && !string.IsNullOrEmpty(companyProfile.Pan) &&
            !string.Equals(request.Pan, companyProfile.Pan, StringComparison.OrdinalIgnoreCase))
            reasons.Add($"Request PAN '{request.Pan}' does not match ROC report PAN '{companyProfile.Pan}'.");

        if (reasons.Count == 0) return;

        request.IsManualReviewRequired = true;
        request.ManualReviewReason = string.Join(" ", reasons);
    }

    /// <summary>Returns false (and quarantines the charge document) if the charge report's own company
    /// identity conflicts with the ROC report's — in that case charge data must NOT be used for enrichment,
    /// to avoid attributing another company's charges to this one.</summary>
    private static bool ValidateRocVsCharge(
        IReadOnlyList<SheetData> rocWorkbook, IReadOnlyList<SheetData> chargeWorkbook,
        RequestDocument chargeDocument, McaRequest request)
    {
        var rocCompanySheet = SheetAliases.Find(rocWorkbook, SheetAliases.CompanyProfile);
        var chargeCompanySheet = SheetAliases.Find(chargeWorkbook, SheetAliases.CompanyProfile);
        if (rocCompanySheet is null || chargeCompanySheet is null) return true;

        var rocCin = ExtractCin(rocCompanySheet);
        var chargeCin = ExtractCin(chargeCompanySheet);
        if (string.IsNullOrEmpty(rocCin) || string.IsNullOrEmpty(chargeCin)) return true;
        if (string.Equals(rocCin, chargeCin, StringComparison.OrdinalIgnoreCase)) return true;

        chargeDocument.UploadStatus = DocumentUploadStatus.Quarantined;
        chargeDocument.QuarantineReason = $"Charge report CIN '{chargeCin}' does not match ROC report CIN '{rocCin}'.";
        request.IsManualReviewRequired = true;
        request.ManualReviewReason = (request.ManualReviewReason is null ? "" : request.ManualReviewReason + " ")
            + "Charge report identity does not match the ROC report; charge enrichment was skipped.";
        return false;
    }

    private static string? ExtractCin(SheetData companySheet)
    {
        foreach (var row in companySheet.Rows)
            if (row.Count > 1 && row[0]?.ToString()?.Trim() == "CIN")
                return row[1]?.ToString()?.Trim();
        return null;
    }

    private void RunSectionParsers(
        IReadOnlyList<SheetData> rocWorkbook, IReadOnlyList<SheetData>? chargeWorkbook, bool chargeIdentityMatches,
        long requestId, long runId, long rocDocumentId, long? chargeDocumentId,
        List<IngestionIssue> issues, ref int itemCount)
    {
        var directorsSheet = SheetAliases.Find(rocWorkbook, SheetAliases.Directors);
        if (directorsSheet is not null)
        {
            var r = DirectorsParser.Parse(directorsSheet, requestId, runId, rocDocumentId);
            db.Directors.AddRange(r.Items);
            Collect(r, issues, ref itemCount);
        }

        var otherDirSheet = SheetAliases.Find(rocWorkbook, SheetAliases.OtherDirectorships);
        if (otherDirSheet is not null)
        {
            var r = OtherDirectorshipsParser.Parse(otherDirSheet, requestId, runId, rocDocumentId);
            db.DirectorAssociations.AddRange(r.Items);
            Collect(r, issues, ref itemCount);
        }

        var directorShSheet = SheetAliases.Find(rocWorkbook, SheetAliases.DirectorShareholding);
        var majorShSheet = SheetAliases.Find(rocWorkbook, SheetAliases.MajorShareholding);
        if (directorShSheet is not null || majorShSheet is not null)
        {
            var r = ShareholdingParser.Parse(directorShSheet, majorShSheet, requestId, runId, rocDocumentId, rocDocumentId);
            db.Shareholdings.AddRange(r.Items);
            Collect(r, issues, ref itemCount);
        }

        var financialSheet = SheetAliases.Find(rocWorkbook, SheetAliases.StandaloneFinancialData);
        if (financialSheet is not null)
        {
            var r = StandaloneFinancialDataParser.Parse(financialSheet, requestId, runId, rocDocumentId);
            db.FinancialYearData.AddRange(r.Items);
            Collect(r, issues, ref itemCount);
        }

        var chargesResult = ChargesParser.Parse(rocWorkbook, chargeWorkbook, chargeIdentityMatches,
            requestId, runId, rocDocumentId, chargeDocumentId);
        db.RocCharges.AddRange(chargesResult.Items);
        Collect(chargesResult, issues, ref itemCount);
        itemCount += chargesResult.Items.Sum(c => c.Events.Count); // events counted too — RowsExtracted reflects real row volume

        var msmeSheet = SheetAliases.Find(rocWorkbook, SheetAliases.Msme);
        if (msmeSheet is not null)
        {
            var r = MsmeParser.Parse(msmeSheet, requestId, runId, rocDocumentId);
            db.MsmePayments.AddRange(r.Items);
            Collect(r, issues, ref itemCount);
        }

        var gstSheet = SheetAliases.Find(rocWorkbook, SheetAliases.Gst);
        var gstAnnexureSheet = SheetAliases.Find(rocWorkbook, SheetAliases.GstAnnexure);
        if (gstSheet is not null || gstAnnexureSheet is not null)
        {
            var gstOutput = GstParser.Parse(gstSheet, gstAnnexureSheet, requestId, runId, rocDocumentId);
            db.GstRegistrations.AddRange(gstOutput.Registrations.Items);
            Collect(gstOutput.Registrations, issues, ref itemCount);
            Collect(gstOutput.Filings, issues, ref itemCount);
        }

        var epfoAnnexureSheet = SheetAliases.Find(rocWorkbook, SheetAliases.EpfoAnnexure);
        if (epfoAnnexureSheet is not null)
        {
            var r = EpfoParser.Parse(epfoAnnexureSheet, requestId, runId, rocDocumentId);
            db.EpfoContributions.AddRange(r.Items);
            Collect(r, issues, ref itemCount);
        }

        var auditorsSheet = SheetAliases.Find(rocWorkbook, SheetAliases.Auditors);
        if (auditorsSheet is not null)
        {
            var r = AuditorsParser.Parse(auditorsSheet, requestId, runId, rocDocumentId);
            db.AuditorObservations.AddRange(r.Items);
            Collect(r, issues, ref itemCount);
        }

        var legalSheet = SheetAliases.Find(rocWorkbook, SheetAliases.LegalHistory);
        if (legalSheet is not null)
        {
            var r = LegalHistoryParser.Parse(legalSheet, requestId, runId, rocDocumentId);
            db.Litigations.AddRange(r.Items);
            Collect(r, issues, ref itemCount);
        }
    }

    private static void Collect<T>(ParseResult<T> result, List<IngestionIssue> issues, ref int itemCount)
    {
        itemCount += result.Items.Count;
        foreach (var w in result.Warnings)
            issues.Add(ToEntity(w));
        foreach (var e in result.Errors)
            issues.Add(ToEntity(e));
    }

    private static IngestionIssue ToEntity(ParseIssue issue) => new()
    {
        Severity = issue.Severity,
        ParserName = issue.ParserName,
        FieldName = issue.FieldName,
        RawValue = issue.RawValue,
        RowNumber = issue.RowNumber,
        IssueCode = issue.IssueCode,
        Message = issue.Message
    };
}

public class IngestionFailedException(string message) : Exception(message);
