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

            // Layer 0 — record every non-blank row of every sheet of the ROC workbook verbatim before
            // any typed parsing. The charge workbook's rows are recorded later, only if its company
            // identity matches (a mismatched charge workbook is another company's data).
            var extractedAt = DateTime.UtcNow;
            db.SourceRows.AddRange(SourceRowRecorder.Record(
                rocWorkbook, "RocReport", requestId, run.IngestionRunId, rocDocumentId, extractedAt));

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
            {
                chargeIdentityMatches = ValidateRocVsCharge(rocWorkbook, chargeWorkbook, chargeDocument!, request);
                if (chargeIdentityMatches)
                    db.SourceRows.AddRange(SourceRowRecorder.Record(
                        chargeWorkbook, "ChargeReport", requestId, run.IngestionRunId,
                        chargeDocumentId!.Value, extractedAt));
            }

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
    /// identity conflicts with the ROC report's on company name, CIN, or PAN — in that case charge data
    /// must NOT be used for enrichment, to avoid attributing another company's charges to this one.
    /// Checking CIN alone isn't sufficient: a charge workbook with a matching or blank CIN but a
    /// mismatched name or PAN would otherwise be accepted and used to enrich the ROC charge data.</summary>
    private static bool ValidateRocVsCharge(
        IReadOnlyList<SheetData> rocWorkbook, IReadOnlyList<SheetData> chargeWorkbook,
        RequestDocument chargeDocument, McaRequest request)
    {
        var rocCompanySheet = SheetAliases.Find(rocWorkbook, SheetAliases.CompanyProfile);
        var chargeCompanySheet = SheetAliases.Find(chargeWorkbook, SheetAliases.CompanyProfile);
        if (rocCompanySheet is null || chargeCompanySheet is null) return true;

        var mismatches = new List<string>();
        CompareField(rocCompanySheet, chargeCompanySheet, "Legal Name", "company name", mismatches, normalize: true);
        CompareField(rocCompanySheet, chargeCompanySheet, "CIN", "CIN", mismatches, normalize: false);
        CompareField(rocCompanySheet, chargeCompanySheet, "PAN", "PAN", mismatches, normalize: false);

        if (mismatches.Count == 0) return true;

        chargeDocument.UploadStatus = DocumentUploadStatus.Quarantined;
        chargeDocument.QuarantineReason = string.Join(" ", mismatches);
        request.IsManualReviewRequired = true;
        request.ManualReviewReason = (request.ManualReviewReason is null ? "" : request.ManualReviewReason + " ")
            + "Charge report identity does not match the ROC report; charge enrichment was skipped.";
        return false;
    }

    private static void CompareField(
        SheetData rocSheet, SheetData chargeSheet, string label, string displayName, List<string> mismatches, bool normalize)
    {
        var rocValue = ExtractField(rocSheet, label);
        var chargeValue = ExtractField(chargeSheet, label);
        if (string.IsNullOrEmpty(rocValue) || string.IsNullOrEmpty(chargeValue)) return;

        var equal = normalize
            ? string.Equals(NameNormalizer.Normalize(rocValue), NameNormalizer.Normalize(chargeValue), StringComparison.Ordinal)
            : string.Equals(rocValue, chargeValue, StringComparison.OrdinalIgnoreCase);

        if (!equal)
            mismatches.Add($"Charge report {displayName} '{chargeValue}' does not match ROC report {displayName} '{rocValue}'.");
    }

    private static string? ExtractField(SheetData companySheet, string label)
    {
        foreach (var row in companySheet.Rows)
            if (row.Count > 1 && row[0]?.ToString()?.Trim() == label)
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
            var r = StandaloneFinancialDataParser.Parse(financialSheet, requestId, runId, rocDocumentId, FinancialBasis.Standalone);
            db.FinancialYearData.AddRange(r.Items);
            Collect(r, issues, ref itemCount);
        }

        var consolidatedSheet = SheetAliases.Find(rocWorkbook, SheetAliases.ConsolidatedFinancialData);
        if (consolidatedSheet is not null)
        {
            var r = StandaloneFinancialDataParser.Parse(consolidatedSheet, requestId, runId, rocDocumentId, FinancialBasis.Consolidated);
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
            var r = AuditorsParser.Parse(auditorsSheet, requestId, runId, rocDocumentId, FinancialBasis.Standalone);
            db.AuditorObservations.AddRange(r.Items);
            Collect(r, issues, ref itemCount);
        }

        var auditorsConsolidatedSheet = SheetAliases.Find(rocWorkbook, SheetAliases.AuditorsConsolidated);
        if (auditorsConsolidatedSheet is not null)
        {
            var r = AuditorsParser.Parse(auditorsConsolidatedSheet, requestId, runId, rocDocumentId, FinancialBasis.Consolidated);
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

        // ── Phase 6 domain sheets ──
        var structureSheet = SheetAliases.Find(rocWorkbook, SheetAliases.Structure);
        if (structureSheet is not null)
        {
            var r = StructureParser.Parse(structureSheet, requestId, runId, rocDocumentId);
            db.CompanyStructures.AddRange(r.Items);
            Collect(r, issues, ref itemCount);
        }

        var relatedSheet = SheetAliases.Find(rocWorkbook, SheetAliases.RelatedCorporates);
        if (relatedSheet is not null)
        {
            var r = RelatedCorporatesParser.Parse(relatedSheet, requestId, runId, rocDocumentId);
            db.RelatedCorporates.AddRange(r.Items);
            Collect(r, issues, ref itemCount);
        }

        var complianceSheet = SheetAliases.Find(rocWorkbook, SheetAliases.Compliance);
        if (complianceSheet is not null)
        {
            var r = ComplianceParser.Parse(complianceSheet, requestId, runId, rocDocumentId);
            db.ComplianceRecords.AddRange(r.Items);
            Collect(r, issues, ref itemCount);
        }

        var allotmentSheet = SheetAliases.Find(rocWorkbook, SheetAliases.SecuritiesAllotment);
        if (allotmentSheet is not null)
        {
            var r = SecuritiesAllotmentParser.Parse(allotmentSheet, requestId, runId, rocDocumentId);
            db.SecurityAllotments.AddRange(r.Items);
            Collect(r, issues, ref itemCount);
        }

        var proprietorshipSheet = SheetAliases.Find(rocWorkbook, SheetAliases.Proprietorship);
        if (proprietorshipSheet is not null)
        {
            var r = ProprietorshipParser.Parse(proprietorshipSheet, requestId, runId, rocDocumentId);
            db.ProprietorshipAssociations.AddRange(r.Items);
            Collect(r, issues, ref itemCount);
        }

        var dirHistorySheet = SheetAliases.Find(rocWorkbook, SheetAliases.DirectorAssociationHistory);
        if (dirHistorySheet is not null)
        {
            var r = DirectorAssociationHistoryParser.Parse(dirHistorySheet, requestId, runId, rocDocumentId);
            db.DirectorAssignmentHistories.AddRange(r.Items);
            Collect(r, issues, ref itemCount);
        }

        var peerSheet = SheetAliases.Find(rocWorkbook, SheetAliases.PeerComparison);
        if (peerSheet is not null)
        {
            var r = PeerComparisonParser.Parse(peerSheet, requestId, runId, rocDocumentId);
            db.PeerComparisonMetrics.AddRange(r.Items);
            Collect(r, issues, ref itemCount);
        }

        var highlightsSheet = SheetAliases.Find(rocWorkbook, SheetAliases.Highlights);
        var financialParamsSheet = SheetAliases.Find(rocWorkbook, SheetAliases.FinancialParametersAnnexure);
        if (highlightsSheet is not null || financialParamsSheet is not null)
        {
            var r = FinancialParametersParser.Parse(highlightsSheet, financialParamsSheet, requestId, runId, rocDocumentId);
            db.FinancialParameters.AddRange(r.Items);
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
