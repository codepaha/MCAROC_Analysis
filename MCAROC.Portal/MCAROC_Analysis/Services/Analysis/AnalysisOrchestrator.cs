using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Analysis;

/// <summary>Drives one analysis pass for a request: atomic claim, rule engine, AI synthesis, persistence.
/// Mirrors IngestionOrchestrator's structure and Phase 2's FilingBatchProcessor's claim/recovery patterns.</summary>
public class AnalysisOrchestrator(AppDbContext db, AiCrossSectionAnalysisService aiService, AnalysisQueue queue, ILogger<AnalysisOrchestrator> logger)
{
    private static readonly RuleThresholds Thresholds = RuleThresholds.Default;
    public const string RuleEngineVersion = "1.0";

    /// <summary>Code prefix for cross-section findings the AI synthesis pass adds after
    /// OverallReviewPriority is already computed and stored — ReviewPriorityCalculator.Explain excludes
    /// any finding with this prefix so it never disagrees with the stored priority.</summary>
    public const string AiCrossSectionCodePrefix = "AI_CROSS_";

    public async Task RunAnalysisAsync(long requestId, CancellationToken ct)
    {
        // Atomic claim, mirroring Phase 2's exact ExecuteUpdateAsync-based claim pattern: only the caller
        // whose UPDATE actually matches a row proceeds, so a double-enqueue (recovery + a near-simultaneous
        // trigger) collapses to one winner rather than creating two AnalysisRuns.
        var claimed = await db.Requests
            .Where(r => r.RequestId == requestId && r.RequestStatus == RequestStatus.DataExtracted)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RequestStatus, RequestStatus.AiAnalysisInProgress), ct);
        if (claimed == 0)
            return;

        var request = await db.Requests.FirstAsync(r => r.RequestId == requestId, ct);
        var runNumber = await db.AnalysisRuns.CountAsync(a => a.RequestId == requestId, ct) + 1;

        if (request.LatestCompletedIngestionRunId is not { } ingestionRunId)
        {
            logger.LogError("Request {RequestId} claimed for analysis but has no LatestCompletedIngestionRunId.", requestId);
            await db.Requests.Where(r => r.RequestId == requestId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.RequestStatus, RequestStatus.AiAnalysisFailed), ct);
            return;
        }

        var run = new AnalysisRun
        {
            RequestId = requestId,
            IngestionRunId = ingestionRunId,
            RunNumber = runNumber,
            Status = AnalysisRunStatus.Running,
            RuleEngineVersion = RuleEngineVersion,
            PromptVersion = AiCrossSectionAnalysisService.PromptVersion,
            StartedDate = DateTime.UtcNow
        };
        db.AnalysisRuns.Add(run);
        await db.SaveChangesAsync(ct); // need AnalysisRunId

        try
        {
            var ctx = await BuildContextAsync(request, ingestionRunId, ct);
            var result = RuleEngine.Evaluate(ctx, Thresholds);

            var findingEntities = result.Findings.Select(f => new AnalysisFinding
            {
                AnalysisRunId = run.AnalysisRunId,
                RequestId = requestId,
                Section = f.Section,
                Severity = f.Severity,
                TemporalStatus = f.TemporalStatus,
                Code = f.Code,
                Title = f.Title,
                SummaryText = f.SummaryText,
                MetricsJson = f.MetricsJson,
                SupportingSignalsJson = f.SupportingSignalsJson,
                SourceReferenceJson = f.SourceReferenceJson,
                PeriodLabel = f.PeriodLabel,
                ObservationDate = f.ObservationDate,
                DisplayPriority = f.DisplayPriority
            }).ToList();
            db.AnalysisFindings.AddRange(findingEntities);

            run.CriticalFindingsCount = result.Findings.Count(f => f.Severity == FindingSeverity.Critical);
            run.ReviewFindingsCount = result.Findings.Count(f => f.Severity == FindingSeverity.Review);
            run.WatchFindingsCount = result.Findings.Count(f => f.Severity == FindingSeverity.Watch);
            run.PositiveFindingsCount = result.Findings.Count(f => f.Severity == FindingSeverity.Positive);
            run.OverallReviewPriority = result.OverallReviewPriority;
            run.DataSufficiencyNotesJson = result.DataSufficiencyNotes.Count > 0
                ? JsonSerializer.Serialize(result.DataSufficiencyNotes.Select(n => new { code = n.Code, reason = n.Reason }))
                : null;

            // Findings + run metadata persist BEFORE the AI call — a crash mid-AI-call still leaves the
            // deterministic rule-engine results intact for the next request to this request (recovery
            // resets and re-runs from scratch rather than resuming, but even if it didn't, nothing here is
            // lost by persisting early).
            await db.SaveChangesAsync(ct);

            var aiOutcome = await aiService.SynthesizeAsync(findingEntities, result.OverallReviewPriority, result.DataSufficiencyNotes, ct);

            if (aiOutcome.Success)
            {
                foreach (var narrative in aiOutcome.FindingNarratives)
                {
                    var target = findingEntities.FirstOrDefault(f => f.Code == narrative.Code);
                    if (target is null) continue;
                    target.WhyThisMatters = narrative.WhyThisMatters;
                    target.RecommendedReview = narrative.RecommendedReview;
                }

                foreach (var cross in aiOutcome.CrossSectionFindings)
                {
                    db.AnalysisFindings.Add(new AnalysisFinding
                    {
                        AnalysisRunId = run.AnalysisRunId,
                        RequestId = requestId,
                        Section = FindingSection.CrossSection,
                        Severity = cross.Severity,
                        TemporalStatus = TemporalStatus.Current,
                        Code = $"{AiCrossSectionCodePrefix}{Guid.NewGuid():N}",
                        Title = cross.Title,
                        SummaryText = cross.Narrative,
                        SupportingSignalsJson = JsonSerializer.Serialize(cross.RelatedCodes)
                    });
                }

                run.ExecutiveSummaryJson = aiOutcome.ExecutiveSummary is { } summary ? JsonSerializer.Serialize(summary) : null;
                run.Status = AnalysisRunStatus.Completed;
            }
            else
            {
                // Rule-engine findings are still persisted and shown — an AI failure never hides the
                // deterministic result, mirroring Phase 2's "AI failure doesn't stop the batch" precedent.
                run.Status = AnalysisRunStatus.CompletedWithErrors;
                run.FailureReason = aiOutcome.FailureReason;
            }

            run.CompletedDate = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            await db.Requests.Where(r => r.RequestId == requestId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.RequestStatus, RequestStatus.AnalysisCompleted), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Analysis failed for request {RequestId}, run {RunNumber}", requestId, runNumber);
            db.ChangeTracker.Clear();

            await db.AnalysisRuns.Where(a => a.AnalysisRunId == run.AnalysisRunId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, AnalysisRunStatus.Failed)
                    .SetProperty(a => a.FailureReason, ex.Message)
                    .SetProperty(a => a.CompletedDate, DateTime.UtcNow), ct);

            await db.Requests.Where(r => r.RequestId == requestId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.RequestStatus, RequestStatus.AiAnalysisFailed), ct);
        }
    }

    /// <summary>Recovers a crash mid-analysis by restarting, not resuming — analysis is cheap (rule
    /// evaluation + one Gemini call), unlike Phase 2's multi-hour OCR pipeline where in-place resume
    /// mattered. The atomic claim in RunAnalysisAsync only matches RequestStatus=DataExtracted, so a
    /// request already at AiAnalysisInProgress from a crashed run would never satisfy it again if simply
    /// re-enqueued — this resets it first.</summary>
    public async Task<int> RecoverStaleWorkAsync(CancellationToken ct)
    {
        var stuckRequestIds = await db.Requests
            .Where(r => r.RequestStatus == RequestStatus.AiAnalysisInProgress)
            .Select(r => r.RequestId)
            .ToListAsync(ct);

        foreach (var requestId in stuckRequestIds)
        {
            await db.AnalysisRuns
                .Where(a => a.RequestId == requestId && a.Status == AnalysisRunStatus.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, AnalysisRunStatus.Failed)
                    .SetProperty(a => a.FailureReason, "Application restarted mid-analysis.")
                    .SetProperty(a => a.CompletedDate, DateTime.UtcNow), ct);

            await db.Requests.Where(r => r.RequestId == requestId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.RequestStatus, RequestStatus.DataExtracted), ct);

            queue.Enqueue(requestId);
        }

        return stuckRequestIds.Count;
    }

    private async Task<AnalysisContext> BuildContextAsync(McaRequest request, long ingestionRunId, CancellationToken ct)
    {
        var companyProfile = await db.CompanyProfiles.FirstOrDefaultAsync(x => x.IngestionRunId == ingestionRunId, ct);
        var directors = await db.Directors.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        var directorAssociations = await db.DirectorAssociations.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        var shareholdings = await db.Shareholdings.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        // The rule engine is standalone-only (Phase 6 added Consolidated rows to the same table).
        var financialYears = await db.FinancialYearData
            .Where(x => x.IngestionRunId == ingestionRunId && x.Basis == FinancialBasis.Standalone).ToListAsync(ct);
        var charges = await db.RocCharges.Include(c => c.Events).Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        var msmePayments = await db.MsmePayments.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        var gstRegistrations = await db.GstRegistrations.Include(g => g.Filings).Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        var epfoContributions = await db.EpfoContributions.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        var auditorObservations = await db.AuditorObservations
            .Where(x => x.IngestionRunId == ingestionRunId && x.Basis == FinancialBasis.Standalone).ToListAsync(ct);
        var litigations = await db.Litigations.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);

        return AnalysisContext.Build(
            request, companyProfile, directors, directorAssociations, shareholdings, financialYears, charges,
            msmePayments, gstRegistrations, epfoContributions, auditorObservations, litigations, DateTime.UtcNow);
    }
}
