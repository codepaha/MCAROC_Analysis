using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.CompanyMaster;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.PreLoginReports;

public sealed record BorrowerIdentityCandidate(string Name, string Identifier, string? Status, string? State, double Score);
public sealed record BorrowerIdentityResolution(string? Identifier, string ReasonCode, double? TopScore,
    IReadOnlyList<BorrowerIdentityCandidate> Candidates);

/// <summary>Read-only MCA-master identity gate for uploaded pre-login assignments. It does not change the
/// post-login resolver's global AutoSelect setting or create a separate McaRequest.</summary>
public sealed class BorrowerAssignmentIdentityResolver(AppDbContext db)
{
    private static readonly ResolverOptions IntakePolicy = new()
    {
        AutoSelectEnabled = true, AutoSelectThreshold = 0.99, MinimumMargin = 0.10,
        AllowSoleActive = false, SuggestionFloor = 0.5, MaxCandidates = 10
    };

    public async Task<BorrowerIdentityResolution> ResolveAsync(BorrowerAssignmentDetails details, string? extractedLlpin,
        CancellationToken ct)
    {
        var c = details.CompanyDetails;
        var type = BorrowerRequestParser.EntityType(details);
        if (type is not (PreLoginReportEntityType.Company or PreLoginReportEntityType.Llp))
            return new(null, "IDENTITY_NOT_APPLICABLE", null, []);
        if (string.IsNullOrWhiteSpace(c.CompanyName))
            return new(null, ResolutionReasonCodes.NameMissing, null, []);
        var expectedType = type == PreLoginReportEntityType.Llp ? CompanyMasterRecordType.Llp : CompanyMasterRecordType.Company;
        var identifier = type == PreLoginReportEntityType.Llp ? extractedLlpin : c.Cin;
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(identifier))
            {
                if (!CompanyNameResolver.IsValidIdentifier(identifier))
                    return new(null, ResolutionReasonCodes.IdentifierInvalid, null, []);
                var row = await CompanyNameCandidateRetriever.GetByIdentifierAsync(connection, identifier, ct);
                if (row is null) return new(null, ResolutionReasonCodes.IdentifierNotInMaster, null, []);
                var ranked = CompanyNameResolver.Rank(c.CompanyName, ResolutionHints.None, [row]);
                var exact = ranked[0].Features.GetValueOrDefault("ExactNormalized") == 1;
                if (row.RecordType != expectedType || !exact)
                    return new(null, ResolutionReasonCodes.NeedsConfirmation, ranked[0].Score, ToCandidates(ranked));
                return new(row.Identifier, ResolutionReasonCodes.UserProvidedIdentifier, ranked[0].Score, ToCandidates(ranked));
            }

            var retrieved = await CompanyNameCandidateRetriever.RetrieveAsync(connection, c.CompanyName, ct);
            var candidates = retrieved.Where(x => x.RecordType == expectedType);
            var scored = CompanyNameResolver.Rank(c.CompanyName, ResolutionHints.None, candidates);
            var decision = CompanyNameResolver.Decide(c.CompanyName, scored, IntakePolicy);
            return new(decision.Status == ResolutionStatus.Resolved ? decision.ChosenIdentifier : null,
                decision.ReasonCode, decision.TopScore, ToCandidates(decision.Candidates));
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static IReadOnlyList<BorrowerIdentityCandidate> ToCandidates(IEnumerable<ScoredCandidate> ranked) =>
        ranked.Take(10).Select(x => new BorrowerIdentityCandidate(x.Candidate.Name, x.Candidate.Identifier,
            x.Candidate.Status, x.Candidate.State, x.Score)).ToList();
}
