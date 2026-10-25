using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.McaFilings;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Freshness;
using MCAROC_Analysis.Services.PropertyParticulars;
using MCAROC_Analysis.Services.LitigationModule.Reading;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace MCAROC_Analysis.Services.RequestDetails;

public sealed class RequestDetailsQuery(AppDbContext db, DossierCache dossierCache,
    ILogger? logger = null,
    Microsoft.Extensions.Options.IOptions<BprLitigationOptions>? bprOptions = null,
    LitigationAiAnalysisOrchestrator? litigationAnalysis = null, ChargeLitigationService? chargeLitigation = null,
    LitigationOrderOutcomeQuery? orderOutcomes = null, FullTextSearchStatus? fullTextStatus = null,
    LitigationReaderGate? readerGate = null,
    ICourtRefreshStatusService? courtRefreshStatus = null)
{
    public async Task<RequestDetailsViewModel?> BuildAsync(long id, long? charge, string? court, string? status,
        int page, string? outcome, long? focusCase, bool hasFullAccess, IServiceProvider? services, CancellationToken ct,
        string? search = null, int pageSize = 25, bool compactLitigation = false)
    {
        var version = await RequestPageVersion.ReadAsync(db, id, ct);
        var request = await db.Requests.AsNoTracking().Include(r => r.Client).FirstOrDefaultAsync(r => r.RequestId == id, ct);
        if (request is null) return null;

        var documents = await db.RequestDocuments.AsNoTracking().Where(d => d.RequestId == id).ToListAsync(ct);
        var vm = new RequestDetailsViewModel { Request = request, Documents = documents, FocusChargeId = charge, FocusCaseId = focusCase };
        vm.HasFullAccess = hasFullAccess;
        vm.AutoFetchJob = await db.AutoFetchJobs.AsNoTracking().FirstOrDefaultAsync(j => j.RequestId == id, ct);
        // #497: the name the request was resolved from, shown when the company is now registered under a different one.
        var applied = await db.IdentityResolutions.AsNoTracking()
            .Where(r => r.RequestId == id && r.ChosenIdentifier != null && r.InputName != null)
            .OrderByDescending(r => r.IdentityResolutionId).Select(r => new { r.InputName, r.ChosenIdentifier }).FirstOrDefaultAsync(ct);
        var askedKey = applied?.InputName is { } a ? Services.CompanyMaster.CompanyNameNormalizer.Normalize(a).NameNormalized : "";
        if (askedKey.Length > 0 && askedKey != Services.CompanyMaster.CompanyNameNormalizer.Normalize(request.CompanyName).NameNormalized)
        {
            vm.RequestedAsName = applied!.InputName;
            // Whoever chose the company (automatically, or a person confirming the suggestion), the name is a former one if it is stored as such.
            vm.RequestedAsFormerName = await db.CompanyFormerNames.AsNoTracking()
                .AnyAsync(f => f.FormerNameNormalized == askedKey && f.Identifier == applied.ChosenIdentifier, ct);
        }

        await new WorkbookDetailsQuery(db, logger).LoadAsync(vm, services, ct);

        await new FilingDetailsQuery(db).LoadAsync(vm, ct);

        if (fullTextStatus is not null)
            vm.KeywordSearchNote = (await fullTextStatus.GetAsync(ct)).ChatNote;

        // Computed metrics (Wave 4). The portal and the dossier PDF read the SAME assembled
        // DossierModel — DossierCache builds it once per (request, ingestion run, analysis run) and
        // hands it to both surfaces — so a portal Key Indicators panel and the PDF's can never diverge.
        // Null (no completed analysis for the latest ingestion) ⇒ no metrics yet; the panel hides.
        var dossier = await dossierCache.GetAsync(id, ct);
        vm.KeyMetrics = dossier?.Metrics.ToList() ?? [];
        vm.DataSufficiencyNotes = dossier?.ExecSummary.NotAssessed.ToList() ?? [];

        // The corporate event timeline is intentionally NOT part of DossierModel/DossierCache — it needs
        // none of the analysis-derived data those require, and must stay populated the moment ingestion
        // completes even while a fresh re-ingest's analysis is still queued or running.
        vm.Timeline = CorporateTimelineBuilder.BuildFrom(vm).ToList();

        var chatSession = await db.ChatSessions.AsNoTracking().FirstOrDefaultAsync(s => s.RequestId == id, ct);
        if (chatSession is not null)
        {
            vm.ChatMessages = await db.ChatMessages.AsNoTracking()
                .Where(m => m.ChatSessionId == chatSession.ChatSessionId)
                .OrderBy(m => m.CreatedDate)
                .ToListAsync(ct);
        }

        // A suggestion that cannot be built must never take the page down: no suggestions then.
        try { vm.ChatSuggestions = await new MCAROC_Analysis.Services.Chat.ChatSuggestions(db,
                (services?.GetService(typeof(Microsoft.Extensions.Options.IOptions<MCAROC_Analysis.Services.Chat.QuestionRepositoryOptions>)) as Microsoft.Extensions.Options.IOptions<MCAROC_Analysis.Services.Chat.QuestionRepositoryOptions>)?.Value)
                .BuildAsync(id, vm.Request.LatestCompletedIngestionRunId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { vm.ChatSuggestions = []; }

        await new LitigationDetailsQuery(db, bprOptions, litigationAnalysis, orderOutcomes, readerGate, courtRefreshStatus)
            .LoadAsync(vm, court, status, page, outcome, focusCase, ct, search, pageSize, compactLitigation);

        // Never let the charge-to-case comparison take the request page down: it is a highlight, not the page.
        if (chargeLitigation is not null)
        {
            try { vm.ChargeLitigation = await chargeLitigation.GetAsync(id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "Charge-litigation comparison failed for request {RequestId}", id);
            }
        }

        if (version != await RequestPageVersion.ReadAsync(db, id, ct)
            || (dossier is not null && dossier.IngestionRunId != vm.Request.LatestCompletedIngestionRunId)
            || (vm.ChargeLitigation is { } links && links.SnapshotId != vm.LitigationDataLake?.AuthoritativeSnapshot?.LitigationReportSnapshotId))
            throw new RequestPageChangedException();
        if (vm.LitigationDataLake?.CaseList is { } list) list.Generation = version?.Key;
        return vm;
    }
}
