using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.CompanyMaster;

/// <param name="Decision">What the resolver decided.</param>
/// <param name="IdentityResolutionId">The audit row written for it.</param>
/// <param name="AppliedToRequest">Whether the request now carries the chosen identifier.</param>
/// <param name="ExistingRequestId">Set when applying collided with this client's existing request for the same
/// company (<c>DUPLICATE_REQUEST</c>) — the caller should point the user at that request.</param>
public sealed record IdentityResolutionResult(
    ResolutionDecision Decision, long IdentityResolutionId, bool AppliedToRequest, long? ExistingRequestId);

/// <summary>I/O half of name → CIN/LLPIN resolution (issue #294): retrieval → <see cref="CompanyNameResolver"/> →
/// an <see cref="IdentityResolution"/> audit row, and — only when an identifier was actually chosen — the
/// request's <c>Cin</c>/<c>Llpin</c>/<c>AutoFetchCompanyIdentifier</c>/<c>EntityType</c>, all in one
/// transaction. Nothing calls this automatically yet: the intake search screen and the coordinator's
/// <c>Resolve</c> stage (#295) are its consumers.</summary>
public sealed class IdentityResolutionService(AppDbContext db, IOptions<ResolverOptions> options)
{
    public const string DuplicateRequestReason = ResolutionReasonCodes.DuplicateRequest;
    public const string IdentifierConflictReason = "REQUEST_ALREADY_IDENTIFIED";

    // Enums as names ("LLP", "Company"), so the audit JSON stays readable without the code at hand.
    private static readonly JsonSerializerOptions Json = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    /// <summary>Ranked suggestions for a name (or a supplied identifier), without persisting anything — for the
    /// interactive intake search.</summary>
    public async Task<ResolutionDecision> SuggestAsync(string? name, string? identifier, ResolutionHints hints, CancellationToken ct = default)
    {
        var connection = await OpenAsync(ct);
        try { return await DecideAsync(connection, name, identifier, hints, ct); }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    /// <summary>Resolves for a request and records the decision; writes the identifier to the request only if the
    /// decision chose one.</summary>
    public async Task<IdentityResolutionResult> ResolveForRequestAsync(
        long requestId, string? name, string? identifier, ResolutionHints hints, string actor, CancellationToken ct = default)
    {
        ResolutionDecision decision;
        var connection = await OpenAsync(ct);
        try { decision = await DecideAsync(connection, name, identifier, hints, ct); }
        finally { await db.Database.CloseConnectionAsync(); }

        return await RecordAsync(requestId, name, identifier, hints, decision, actor, ct);
    }

    /// <summary>A person picked one of the candidates (intake search or the needs-attention board). The pick
    /// must exist in the master; it is recorded as <see cref="ResolutionMethod.HumanSelected"/>.</summary>
    public async Task<IdentityResolutionResult> ApplyHumanSelectionAsync(
        long requestId, string chosenIdentifier, string? inputName, ResolutionHints hints, string actor, CancellationToken ct = default)
    {
        MasterCandidate? row;
        var connection = await OpenAsync(ct);
        try { row = await CompanyNameCandidateRetriever.GetByIdentifierAsync(connection, chosenIdentifier, ct); }
        finally { await db.Database.CloseConnectionAsync(); }

        var provided = CompanyNameResolver.ForProvidedIdentifier(chosenIdentifier, row);
        var decision = provided.Status == ResolutionStatus.Resolved
            ? provided with { Method = ResolutionMethod.HumanSelected, ReasonCode = ResolutionReasonCodes.HumanSelected }
            : provided;
        return await RecordAsync(requestId, inputName, chosenIdentifier, hints, decision, actor, ct);
    }

    private async Task<ResolutionDecision> DecideAsync(SqlConnection connection, string? name, string? identifier, ResolutionHints hints, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(identifier))
            return CompanyNameResolver.ForProvidedIdentifier(identifier,
                await CompanyNameCandidateRetriever.GetByIdentifierAsync(connection, identifier, ct));

        var candidates = await CompanyNameCandidateRetriever.RetrieveAsync(connection, name ?? "", ct);
        var ranked = CompanyNameResolver.Rank(name ?? "", hints, candidates);
        return CompanyNameResolver.Decide(name ?? "", ranked, options.Value);
    }

    private async Task<IdentityResolutionResult> RecordAsync(
        long requestId, string? name, string? identifier, ResolutionHints hints, ResolutionDecision decision, string actor, CancellationToken ct)
    {
        var snapshot = await db.CompanyMasterSyncJobs.AsNoTracking()
            .Where(j => j.Status == CompanyMasterSyncJobStatus.Completed)
            .MaxAsync(j => j.PublishedDate, ct);
        var chosen = decision.Status == ResolutionStatus.Resolved ? decision.ChosenIdentifier : null;
        var chosenRow = chosen is null ? null : decision.Candidates.FirstOrDefault(c => c.Candidate.Identifier == chosen)?.Candidate;

        var resolution = new IdentityResolution
        {
            RequestId = requestId,
            InputName = Truncate(name?.Trim(), 400),
            InputIdentifier = Truncate(identifier?.Trim().ToUpperInvariant(), 30),
            HintsJson = JsonSerializer.Serialize(hints, Json),
            NormalizedInput = decision.NormalizedInput.NameNormalized.Length > 0 ? decision.NormalizedInput.NameNormalized : null,
            Status = decision.Status,
            Method = decision.Method,
            ChosenIdentifier = chosen,
            RecommendedIdentifier = decision.RecommendedIdentifier,
            AutoSelectEligible = decision.AutoSelectEligible,
            TopScore = decision.TopScore,
            Margin = decision.Margin,
            ReasonCode = decision.ReasonCode,
            CandidatesJson = SerializeCandidates(decision.Candidates),
            AlgorithmVersion = CompanyNameResolver.AlgorithmVersion,
            NormalizerVersion = CompanyNameNormalizer.Version,
            OptionsJson = JsonSerializer.Serialize(options.Value, Json),
            MasterSnapshotDate = snapshot,
            CreatedBy = actor,
            CreatedUtc = DateTime.UtcNow,
        };

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var request = await db.Requests.SingleAsync(r => r.RequestId == requestId, ct);

        if (chosenRow is not null)
        {
            if (request.AutoFetchCompanyIdentifier is { } current && !string.Equals(current, chosenRow.Identifier, StringComparison.OrdinalIgnoreCase))
            {
                // Never silently re-point a request that already has a different company.
                resolution.ReasonCode = IdentifierConflictReason;
            }
            else
            {
                var existing = await FindOtherRequestAsync(request, chosenRow.Identifier, ct);
                if (existing is not null)
                {
                    resolution.ReasonCode = DuplicateRequestReason;
                    resolution.ExistingRequestId = existing;
                }
                else
                {
                    Apply(request, chosenRow);
                    resolution.AppliedToRequest = true;
                }
            }
        }

        db.IdentityResolutions.Add(resolution);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException) when (resolution.AppliedToRequest)
        {
            // The client-scoped unique index on AutoFetchCompanyIdentifier is the concurrency backstop for the
            // pre-check above: another request took this company between the read and the write. Record the
            // resolution against the existing request instead of throwing (plan §5A.2 step 5).
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var reloaded = await db.Requests.AsNoTracking().SingleAsync(r => r.RequestId == requestId, ct);
            var holder = await FindOtherRequestAsync(reloaded, chosenRow!.Identifier, ct);
            if (holder is null) throw; // not the unique-index race — don't disguise another failure as a duplicate

            await using var retry = await db.Database.BeginTransactionAsync(ct);
            resolution.IdentityResolutionId = 0;
            resolution.AppliedToRequest = false;
            resolution.ReasonCode = DuplicateRequestReason;
            resolution.ExistingRequestId = holder;
            db.IdentityResolutions.Add(resolution);
            await db.SaveChangesAsync(ct);
            await retry.CommitAsync(ct);
        }

        return new IdentityResolutionResult(decision, resolution.IdentityResolutionId, resolution.AppliedToRequest, resolution.ExistingRequestId);
    }

    /// <summary>Same identity rule as AutoFetch intake: CIN goes to <c>Cin</c> for both entity types (the existing
    /// convention), LLPIN additionally to <c>Llpin</c>.</summary>
    private static void Apply(McaRequest request, MasterCandidate row)
    {
        var isLlp = row.RecordType == CompanyMasterRecordType.Llp;
        request.EntityType = isLlp ? EntityType.LLP : EntityType.Company;
        request.Cin = row.Identifier;
        request.Llpin = isLlp ? row.Identifier : null;
        request.AutoFetchCompanyIdentifier = row.Identifier;
    }

    private Task<long?> FindOtherRequestAsync(McaRequest request, string identifier, CancellationToken ct) =>
        db.Requests.AsNoTracking()
            .Where(r => r.ClientId == request.ClientId && r.RequestId != request.RequestId && r.AutoFetchCompanyIdentifier == identifier)
            .Select(r => (long?)r.RequestId)
            .FirstOrDefaultAsync(ct);

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        return (SqlConnection)db.Database.GetDbConnection();
    }

    private static string SerializeCandidates(IReadOnlyList<ScoredCandidate> candidates) =>
        JsonSerializer.Serialize(candidates.Select(c => new
        {
            identifier = c.Candidate.Identifier,
            name = c.Candidate.Name,
            recordType = c.Candidate.RecordType.ToString(),
            status = c.Candidate.Status,
            state = c.Candidate.State,
            district = c.Candidate.District,
            registrationDate = c.Candidate.RegistrationDate,
            toolOnly = c.Candidate.IsToolOnly,
            score = c.Score,
            features = c.Features,
            reasons = c.Reasons,
        }), Json);

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
