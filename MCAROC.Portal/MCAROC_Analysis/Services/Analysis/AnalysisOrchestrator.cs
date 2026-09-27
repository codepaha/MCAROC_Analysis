using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.CalculationAssurance;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Analysis;

public sealed class AnalysisRunCancelledException(long analysisRunId)
    : Exception($"Analysis run {analysisRunId} was cancelled by a pipeline cancellation.");

/// <summary>Drives one analysis pass for a request: atomic claim, rule engine, AI synthesis, persistence.
/// Mirrors IngestionOrchestrator's structure and Phase 2's FilingBatchProcessor's claim/recovery patterns.</summary>
public class AnalysisOrchestrator(
    AppDbContext db,
    AiCrossSectionAnalysisService aiService,
    AiChargesNarrativeService chargesNarrativeService,
    AnalysisQueue queue,
    CalculationLedgerService calculationLedgerService,
    CalculationCheckRunnerService calculationCheckRunnerService,
    CalculationAiAuditOrchestrator calculationAiAuditOrchestrator,
    ILogger<AnalysisOrchestrator> logger)
{
    private static readonly RuleThresholds Thresholds = RuleThresholds.Default;
    public const string RuleEngineVersion = "1.0";

    /// <summary>Code prefix for cross-section findings the AI synthesis pass adds after
    /// OverallReviewPriority is already computed and stored — ReviewPriorityCalculator.Explain excludes
    /// any finding with this prefix so it never disagrees with the stored priority.</summary>
    public const string AiCrossSectionCodePrefix = "AI_CROSS_";

    private IQueryable<AnalysisRun> AnalysisRunGuarded(long analysisRunId, long requestId) =>
        db.AnalysisRuns
            .Where(a => a.AnalysisRunId == analysisRunId
                && a.Status == AnalysisRunStatus.Running
                && !db.Requests.Any(r => r.RequestId == requestId && r.RequestStatus == RequestStatus.Cancelled));

    private async Task EnsureNotCancelledAsync(long analysisRunId, long requestId, CancellationToken ct)
    {
        var valid = await AnalysisRunGuarded(analysisRunId, requestId).AnyAsync(ct);
        if (!valid)
        {
            db.ChangeTracker.Clear();
            throw new AnalysisRunCancelledException(analysisRunId);
        }
    }

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

        var request = await db.Requests.Include(r => r.Client).FirstAsync(r => r.RequestId == requestId, ct);
        var runNumber = await db.AnalysisRuns.CountAsync(a => a.RequestId == requestId, ct) + 1;

        if (request.LatestCompletedIngestionRunId is not { } ingestionRunId)
        {
            logger.LogError("Request {RequestId} claimed for analysis but has no LatestCompletedIngestionRunId.", requestId);
            await db.Requests.Where(r => r.RequestId == requestId && r.RequestStatus != RequestStatus.Cancelled)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.RequestStatus, RequestStatus.AiAnalysisFailed), ct);
            return;
        }

        var requestStillValid = await db.Requests
            .AnyAsync(r => r.RequestId == requestId && r.RequestStatus != RequestStatus.Cancelled, ct);
        if (!requestStillValid)
            return;

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
            await EnsureNotCancelledAsync(run.AnalysisRunId, requestId, ct);

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

            // Detach run so that SaveChangesAsync will NOT write in-memory run status,
            // which could overwrite an operator cancellation.
            await EnsureNotCancelledAsync(run.AnalysisRunId, requestId, ct);
            db.Entry(run).State = EntityState.Detached;
            await db.SaveChangesAsync(ct);

            var metadataUpdated = await AnalysisRunGuarded(run.AnalysisRunId, requestId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.CriticalFindingsCount, result.Findings.Count(f => f.Severity == FindingSeverity.Critical))
                    .SetProperty(a => a.ReviewFindingsCount, result.Findings.Count(f => f.Severity == FindingSeverity.Review))
                    .SetProperty(a => a.WatchFindingsCount, result.Findings.Count(f => f.Severity == FindingSeverity.Watch))
                    .SetProperty(a => a.PositiveFindingsCount, result.Findings.Count(f => f.Severity == FindingSeverity.Positive))
                    .SetProperty(a => a.OverallReviewPriority, result.OverallReviewPriority)
                    .SetProperty(a => a.DataSufficiencyNotesJson, result.DataSufficiencyNotes.Count > 0
                        ? JsonSerializer.Serialize(result.DataSufficiencyNotes.Select(n => new { code = n.Code, reason = n.Reason }))
                        : null), ct);

            if (metadataUpdated == 0)
            {
                db.ChangeTracker.Clear();
                throw new AnalysisRunCancelledException(run.AnalysisRunId);
            }

            // Calculation-assurance ledger persistence (#164) — deliberately BEFORE the AI call, for the
            // exact same reason as the findings save just above: an AI timeout/crash must never leave a
            // "completed" analysis with no audit ledger at all. Isolated in its own try/catch so a bug in
            // this second-line guardrail can never fail an otherwise-successful analysis pass. A no-op
            // entirely when CalculationAssurance:Mode is Off (see CalculationLedgerService).
            try
            {
                await EnsureNotCancelledAsync(run.AnalysisRunId, requestId, ct);
                await calculationLedgerService.PersistSnapshotAsync(requestId, ingestionRunId, run.AnalysisRunId, ct);
                await calculationCheckRunnerService.RunChecksAsync(requestId, ingestionRunId, run.AnalysisRunId, ct);
                // AI second-line review (#164 PR3) is enqueued, never awaited-to-completion, here — it must
                // never block analysis completion the way the deterministic ledger/checks above correctly
                // do run synchronously. A no-op when Mode is Off or AiAuditEnabled is false.
                await calculationAiAuditOrchestrator.EnqueueForSnapshotAsync(requestId, ingestionRunId, run.AnalysisRunId, ct);
            }
            catch (AnalysisRunCancelledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Calculation-assurance ledger/check/AI-audit-enqueue persistence failed for request {RequestId}, analysis run {AnalysisRunId} — analysis itself still proceeding.",
                    requestId, run.AnalysisRunId);
            }

            await EnsureNotCancelledAsync(run.AnalysisRunId, requestId, ct);
            var aiOutcome = await aiService.SynthesizeAsync(findingEntities, result.OverallReviewPriority, result.DataSufficiencyNotes, ct);
            await EnsureNotCancelledAsync(run.AnalysisRunId, requestId, ct);

            string? executiveSummaryJson = null;
            AnalysisRunStatus finalStatus;
            string? failureReason = null;

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

                executiveSummaryJson = aiOutcome.ExecutiveSummary is { } summary ? JsonSerializer.Serialize(summary) : null;
                finalStatus = AnalysisRunStatus.Completed;
            }
            else
            {
                // Rule-engine findings are still persisted and shown — an AI failure never hides the
                // deterministic result, mirroring Phase 2's "AI failure doesn't stop the batch" precedent.
                finalStatus = AnalysisRunStatus.CompletedWithErrors;
                failureReason = aiOutcome.FailureReason;
            }

            // Charges narrative (Feature 2) — a wholly separate AI call from the cross-section synthesis
            // above, in its own try/catch: a failure here must never turn an otherwise-successful analysis
            // into CompletedWithErrors, so run.Status is never touched in this block.
            string? chargesNarrativeJson = null;
            try
            {
                await EnsureNotCancelledAsync(run.AnalysisRunId, requestId, ct);
                var runCharges = await db.RocCharges.Include(c => c.Events)
                    .Where(c => c.RequestId == requestId && c.IngestionRunId == ingestionRunId).ToListAsync(ct);
                var openCharges = DossierComputations.OpenChargesByAmount(runCharges);
                var selected = AiChargesNarrativeService.SelectChargesForNarrative(openCharges);
                var chargesOutcome = await chargesNarrativeService.SynthesizeAsync(selected, openCharges.Count, ct);
                if (chargesOutcome.Success)
                    chargesNarrativeJson = JsonSerializer.Serialize(chargesOutcome.Narrative);
            }
            catch (AnalysisRunCancelledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Charges-narrative synthesis failed for request {RequestId}, analysis run {AnalysisRunId} — analysis itself still proceeding.",
                    requestId, run.AnalysisRunId);
            }

            await EnsureNotCancelledAsync(run.AnalysisRunId, requestId, ct);

            // Persist narrative updates to findings and new cross-section findings (run entity is detached)
            await db.SaveChangesAsync(ct);

            var completedNow = DateTime.UtcNow;
            var updatedRun = await AnalysisRunGuarded(run.AnalysisRunId, requestId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, finalStatus)
                    .SetProperty(a => a.FailureReason, failureReason)
                    .SetProperty(a => a.ExecutiveSummaryJson, executiveSummaryJson)
                    .SetProperty(a => a.ChargesNarrativeJson, chargesNarrativeJson)
                    .SetProperty(a => a.CompletedDate, completedNow), ct);

            if (updatedRun == 0)
            {
                db.ChangeTracker.Clear();
                throw new AnalysisRunCancelledException(run.AnalysisRunId);
            }

            var updatedReq = await db.Requests
                .Where(r => r.RequestId == requestId && r.RequestStatus != RequestStatus.Cancelled)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.RequestStatus, RequestStatus.AnalysisCompleted), ct);

            if (updatedReq == 0)
            {
                db.ChangeTracker.Clear();
                throw new AnalysisRunCancelledException(run.AnalysisRunId);
            }
        }
        catch (AnalysisRunCancelledException)
        {
            logger.LogWarning("Analysis run {AnalysisRunId} for request {RequestId} was cancelled mid-processing; stopping without writing state.",
                run.AnalysisRunId, requestId);
            db.ChangeTracker.Clear();

            await db.AnalysisRuns
                .Where(a => a.AnalysisRunId == run.AnalysisRunId && a.Status == AnalysisRunStatus.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, AnalysisRunStatus.Failed)
                    .SetProperty(a => a.FailureReason, "Run cancelled: Pipeline run was cancelled")
                    .SetProperty(a => a.CompletedDate, DateTime.UtcNow), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Analysis failed for request {RequestId}, run {RunNumber}", requestId, runNumber);
            db.ChangeTracker.Clear();

            var updatedRun = await AnalysisRunGuarded(run.AnalysisRunId, requestId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, AnalysisRunStatus.Failed)
                    .SetProperty(a => a.FailureReason, ex.Message)
                    .SetProperty(a => a.CompletedDate, DateTime.UtcNow), CancellationToken.None);

            if (updatedRun > 0)
            {
                await db.Requests.Where(r => r.RequestId == requestId && r.RequestStatus != RequestStatus.Cancelled)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.RequestStatus, RequestStatus.AiAnalysisFailed), CancellationToken.None);
            }
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
            await RetryAsync(requestId, "Application restarted mid-analysis.", ct);
        }

        return stuckRequestIds.Count;
    }

    /// <summary>Resets any running analysis run to Failed and re-queues the request for analysis.</summary>
    public async Task RetryAsync(long requestId, string? reason = null, CancellationToken ct = default)
    {
        await db.AnalysisRuns
            .Where(a => a.RequestId == requestId && a.Status == AnalysisRunStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, AnalysisRunStatus.Failed)
                .SetProperty(a => a.FailureReason, reason ?? "Manual retry triggered.")
                .SetProperty(a => a.CompletedDate, DateTime.UtcNow), ct);

        var updated = await db.Requests.Where(r => r.RequestId == requestId && r.RequestStatus != RequestStatus.Cancelled)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RequestStatus, RequestStatus.DataExtracted), ct);

        if (updated > 0)
        {
            queue.Enqueue(requestId);
        }
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
        var epfoEstablishments = await db.EpfoEstablishments.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        var auditorObservations = await db.AuditorObservations
            .Where(x => x.IngestionRunId == ingestionRunId && x.Basis == FinancialBasis.Standalone).ToListAsync(ct);
        var litigations = await db.Litigations.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);

        return AnalysisContext.Build(
            request, companyProfile, directors, directorAssociations, shareholdings, financialYears, charges,
            msmePayments, gstRegistrations, epfoContributions, auditorObservations, litigations, DateTime.UtcNow,
            epfoEstablishments);
    }
}
