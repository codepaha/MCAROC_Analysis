using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.CompanyMaster;

/// <summary>What the coordinator needs to know about one recorded <see cref="IdentityResolution"/> — plain data, so
/// the pipeline decider and <see cref="IdentityTrust"/> stay pure.</summary>
public sealed record IdentityFacts(
    long ResolutionId,
    ResolutionStatus Status,
    ResolutionMethod? Method,
    string ReasonCode,
    bool AppliedToRequest,
    string? ChosenIdentifier,
    string? RecommendedIdentifier,
    double? TopScore,
    long? ExistingRequestId)
{
    public bool IsFalseAccept => ReasonCode == ResolutionReasonCodes.FalseAccept;

    public static async Task<IdentityFacts?> LatestAsync(AppDbContext db, long requestId, bool appliedOnly, CancellationToken ct) =>
        await db.IdentityResolutions.AsNoTracking()
            .Where(r => r.RequestId == requestId && (!appliedOnly || r.AppliedToRequest))
            .OrderByDescending(r => r.CreatedUtc).ThenByDescending(r => r.IdentityResolutionId)
            .Select(r => new IdentityFacts(r.IdentityResolutionId, r.Status, r.Method, r.ReasonCode, r.AppliedToRequest,
                r.ChosenIdentifier, r.RecommendedIdentifier, r.TopScore, r.ExistingRequestId))
            .FirstOrDefaultAsync(ct);
}

public sealed record SpendTrust(bool Trusted, string ReasonCode, string Detail);

/// <summary>The trust ladder for unattended spend (issue #295, plan §5A.3): which resolved identities may drive
/// <c>AutoUnlock</c> and the automatic litigation search. A human's approval or button press is never gated by
/// this — it only decides what the automation may do on its own.
/// <list type="bullet">
/// <item><c>UserProvidedCin</c> or <c>HumanSelected</c>: trusted — a person named the company.</item>
/// <item>No resolution recorded: trusted. Every request created before the resolver existed, and every one
/// created through today's auto-fetch form, got its CIN/LLPIN typed or picked by the user.</item>
/// <item><c>AutoSelected</c>: trusted only at or above <see cref="ResolverOptions.SpendThreshold"/>; below it the
/// request still runs every free step, and a human can approve the spend.</item>
/// </list></summary>
public static class IdentityTrust
{
    public const string BelowSpendThreshold = "IDENTITY_BELOW_SPEND_THRESHOLD";

    /// <param name="applied">The request's latest resolution that wrote its identifier, if any.</param>
    public static SpendTrust Evaluate(IdentityFacts? applied, double spendThreshold)
    {
        if (applied is null)
            return new SpendTrust(true, "IDENTIFIED_AT_INTAKE", "The CIN/LLPIN was entered or picked when the request was created.");
        return applied.Method switch
        {
            ResolutionMethod.UserProvidedCin => new SpendTrust(true, applied.ReasonCode, "The CIN/LLPIN was supplied by the requester."),
            ResolutionMethod.HumanSelected => new SpendTrust(true, applied.ReasonCode, "A person selected the company."),
            ResolutionMethod.AutoSelected when applied.TopScore is { } score && score >= spendThreshold =>
                new SpendTrust(true, applied.ReasonCode, $"Auto-selected with score {score:0.000}, at or above the spend threshold {spendThreshold:0.000}."),
            ResolutionMethod.AutoSelected => new SpendTrust(false, BelowSpendThreshold,
                $"Auto-selected with score {applied.TopScore?.ToString("0.000") ?? "unknown"}, below the spend threshold {spendThreshold:0.000}: " +
                "only free steps run on their own; a person must approve anything that spends."),
            _ => new SpendTrust(false, BelowSpendThreshold, "The resolution records no selection method.")
        };
    }
}
