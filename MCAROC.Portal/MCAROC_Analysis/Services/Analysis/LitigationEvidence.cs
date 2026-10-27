using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Reading;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Analysis;

/// <summary>A request's litigation as the rules read it: the litigation service's cases, exactly the set the Litigation tab's case list
/// shows (the company's cases, through the same reader gate), and the company's current and former names that decide its side of a case.
/// The workbook's own legal-history sheets are not read (owner, 2026-10-26): one source, so a finding can never count cases the tab does
/// not list. <see cref="Searched"/> is false until a search has completed: until then the litigation rules are not evaluated, rather than
/// reporting "no litigation".</summary>
/// <param name="LiveDataVersion">The request's live-data version, read before the cases: a change during the read moves the version again,
/// so the next refresh catches it.</param>
public sealed record LitigationEvidence(bool Searched, IReadOnlyList<LitigationCase> Cases, IReadOnlyList<string> CompanyNames, long LiveDataVersion = 0)
{
    public static readonly LitigationEvidence NotSearched = new(false, [], []);

    public static bool IsPending(LitigationCase c) =>
        LitigationCaseStatusClassifier.Classify(c.CaseStatus, c.CaseStage) == LitigationCaseStatusBucket.Pending;

    /// <summary>The company's side as the case page, the report and the chat show it.</summary>
    public LitigationCompanySide SideOf(LitigationCase c) =>
        LitigationCasePresentation.Side(c.Direction, c.PetitionersJson, c.RespondentsJson, CompanyNames);

    /// <summary>Every party named on either side.</summary>
    public static IReadOnlyList<string> PartiesOf(LitigationCase c) =>
        [.. LitigationCaseCardMapper.ParseNames(c.PetitionersJson), .. LitigationCaseCardMapper.ParseNames(c.RespondentsJson)];

    /// <summary>The case as it is cited ("WP 11374/2024").</summary>
    public static string? Cite(LitigationCase c) => LitigationCasePresentation.CaseTitle(c.CaseNumber, c.CaseType, c.CaseYear) ?? c.CaseNumber;

    public static async Task<LitigationEvidence> LoadAsync(AppDbContext db, LitigationReaderGate? gate, long requestId, string? companyName,
        long? ingestionRunId, CancellationToken ct)
    {
        var version = await LitigationModule.Refresh.RequestLiveData.ReadAsync(db, requestId, ct);
        var snapshotId = await LitigationSnapshotSelection.ForRequest(db, requestId).Select(s => (long?)s.LitigationReportSnapshotId).FirstOrDefaultAsync(ct);
        if (snapshotId is not { } sid) return NotSearched with { LiveDataVersion = version };

        var source = gate is null ? CaseSetSource.Legacy : await gate.ResolveCaseListAsync(requestId, ct);
        var caseIds = await LitigationCaseSets.ForAsync(db, source, requestId, sid, ct);
        var cases = await db.LitigationCases.AsNoTracking().Where(c => caseIds.Contains(c.LitigationCaseId))
            .OrderBy(c => c.LitigationCaseId).ToListAsync(ct);

        var identifier = await db.Requests.AsNoTracking().Where(r => r.RequestId == requestId).Select(r => r.Cin ?? r.Llpin).FirstOrDefaultAsync(ct);
        var names = await LitigationCompanyNames.ForAsync(db, requestId, companyName, identifier, ingestionRunId, ct);
        return new LitigationEvidence(true, cases, names, version);
    }
}
