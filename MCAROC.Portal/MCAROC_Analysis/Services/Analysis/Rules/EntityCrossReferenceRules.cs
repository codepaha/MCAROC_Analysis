using System.Text.Json;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Analysis.Rules;

/// <summary>Cross-references names already captured across independent domains within the same request —
/// charge holders, litigation parties, directors/personal guarantors, and the requesting Client — surfacing
/// matches that today sit in unconnected free-text fields (see issue #192). Company-name matches (Checks 1
/// and 3) are structural facts once normalized, not probabilistic guesses. Person-name matching (Check 2)
/// is deliberately precision-biased: it matches on a name's single most distinctive token rather than any
/// shared token, to avoid a common name component (e.g. "KUMAR") producing a false "this director is
/// personally litigated" claim. Mirrors LitigationRules.DetermineRole's fail-closed substring-matching
/// style — prefers a name genuinely appearing in free text over trying to parse that text into structured
/// parties (most real Litigants values don't have a clean two-party shape at all).</summary>
public static partial class EntityCrossReferenceRules
{
    public const string LitigationByExistingChargeHolderCode = "LITIGATION_BY_EXISTING_CHARGE_HOLDER";
    public const string LitigationInvolvesDirectorOrGuarantorCode = "LITIGATION_INVOLVES_DIRECTOR_OR_GUARANTOR";
    public const string ChargeHolderIsRequestingClientCode = "CHARGE_HOLDER_IS_REQUESTING_CLIENT";

    private static readonly string[] CorporateSuffixes = ["LIMITED", "LTD", "PRIVATE", "PVT"];
    private static readonly string[] PersonTitles = ["MR", "MRS", "MS", "SHRI", "SMT", "M/S", "DR"];

    /// <summary>A trailing designation clause after a comma — e.g. "Personal guarantee of Mr. S. Surendra,
    /// Managing Director" — is describing the guarantor just named, not naming a second person. Confirmed as
    /// a real false-extraction risk against live Coastal data: without this filter, "Managing Director" was
    /// extracted as if it were a guarantor's own name. Comparison is on the full stripped-titles segment, not
    /// a token overlap, so a genuine name that happens to contain one of these words is never filtered.</summary>
    private static readonly string[] DesignationPhrases =
    [
        "MANAGING DIRECTOR", "WHOLE TIME DIRECTOR", "WHOLE-TIME DIRECTOR", "JOINT MANAGING DIRECTOR",
        "EXECUTIVE DIRECTOR", "DIRECTOR", "CHAIRMAN", "CHAIRMAN AND MANAGING DIRECTOR", "CEO",
        "MANAGING PARTNER", "PARTNER", "PROPRIETOR", "PROMOTER"
    ];

    public static List<RuleEvaluationOutcome> Evaluate(AnalysisContext ctx) =>
    [
        EvaluateChargeHolderVsLitigants(ctx),
        EvaluateDirectorOrGuarantorVsLitigants(ctx),
        EvaluateChargeHolderVsClient(ctx)
    ];

    // ── Check 1: charge holder ↔ litigant ──────────────────────────────────────────────────────────

    private static RuleEvaluationOutcome EvaluateChargeHolderVsLitigants(AnalysisContext ctx)
    {
        if (ctx.Charges.Count == 0 || ctx.Litigations.Count == 0)
            return RuleEvaluationOutcome.NotEvaluated(LitigationByExistingChargeHolderCode, "No charge or litigation records available.");

        // Satisfied charges are historical exposure, not a current holder — describing a lender repaid
        // years ago as "an existing charge holder" would be a real, present-tense factual error.
        var holderNames = ctx.Charges
            .Where(c => c.SatisfactionDate is null)
            .Select(c => c.LatestChargeHolderRaw)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var matches = new List<(Litigation Litigation, string HolderName)>();
        foreach (var lit in ConfirmedPendingLitigations(ctx))
        {
            if (string.IsNullOrWhiteSpace(lit.Litigants)) continue;
            var normLitigants = NormalizeCompanyName(lit.Litigants);

            foreach (var holder in holderNames)
            {
                var normHolder = NormalizeCompanyName(holder);
                if (normHolder.Length < 4) continue; // too short/generic to trust a substring match
                if (normLitigants.Contains(normHolder, StringComparison.Ordinal))
                {
                    matches.Add((lit, holder));
                    break; // one confirmed holder is enough to flag this litigation row
                }
            }
        }

        if (matches.Count == 0)
            return RuleEvaluationOutcome.NotTriggered();

        var distinctHolders = matches.Select(m => m.HolderName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var summary = distinctHolders.Count == 1
            ? $"{distinctHolders[0]}, a secured charge holder on this company, is also named in {matches.Count} litigation record(s)."
            : $"{distinctHolders.Count} existing charge holders (including {distinctHolders[0]}) are also named across {matches.Count} litigation record(s).";

        return RuleEvaluationOutcome.Triggered(new FindingDraft(
            FindingSection.Litigation, FindingSeverity.Review, TemporalStatus.Current,
            LitigationByExistingChargeHolderCode, "Litigation Involves an Existing Charge Holder",
            summary,
            MetricsJson: JsonSerializer.Serialize(new
            {
                matchCount = matches.Count,
                chargeHolders = distinctHolders,
                caseNumbers = matches.Select(m => m.Litigation.CaseNumber).Distinct()
            }),
            SourceReferenceJson: LitigationRef(matches.Select(m => m.Litigation).Distinct())));
    }

    // ── Check 2: director/guarantor ↔ litigant ─────────────────────────────────────────────────────

    private static RuleEvaluationOutcome EvaluateDirectorOrGuarantorVsLitigants(AnalysisContext ctx)
    {
        if (ctx.Litigations.Count == 0)
            return RuleEvaluationOutcome.NotEvaluated(LitigationInvolvesDirectorOrGuarantorCode, "No litigation records available.");

        var candidateNames = ctx.Directors
            .Select(d => d.NameRaw)
            .Concat(ctx.Charges.SelectMany(c => c.Events).SelectMany(e => ExtractPersonalGuarantors(e.PropertyParticulars)))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidateNames.Count == 0)
            return RuleEvaluationOutcome.NotEvaluated(LitigationInvolvesDirectorOrGuarantorCode, "No director or personal-guarantor names available.");

        var matches = new List<(Litigation Litigation, string PersonName)>();
        foreach (var lit in ConfirmedPendingLitigations(ctx))
        {
            if (string.IsNullOrWhiteSpace(lit.Litigants)) continue;
            var litigantTokens = Tokenize(lit.Litigants);

            foreach (var name in candidateNames)
            {
                if (MatchesOnDistinctiveToken(name, litigantTokens))
                {
                    matches.Add((lit, name));
                    break; // one confirmed person is enough to flag this litigation row
                }
            }
        }

        if (matches.Count == 0)
            return RuleEvaluationOutcome.NotTriggered();

        var distinctPeople = matches.Select(m => m.PersonName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var summary = distinctPeople.Count == 1
            ? $"{distinctPeople[0]} (a director or personal guarantor of this company) is also named in {matches.Count} litigation record(s)."
            : $"{distinctPeople.Count} directors/personal guarantors (including {distinctPeople[0]}) are also named across {matches.Count} litigation record(s).";

        return RuleEvaluationOutcome.Triggered(new FindingDraft(
            FindingSection.Litigation, FindingSeverity.Watch, TemporalStatus.Current,
            LitigationInvolvesDirectorOrGuarantorCode, "Litigation Involves a Director or Personal Guarantor",
            summary,
            MetricsJson: JsonSerializer.Serialize(new
            {
                matchCount = matches.Count,
                people = distinctPeople,
                caseNumbers = matches.Select(m => m.Litigation.CaseNumber).Distinct()
            }),
            SourceReferenceJson: LitigationRef(matches.Select(m => m.Litigation).Distinct())));
    }

    /// <summary>Extracts personal-guarantor names from free text like "Personal Guarantee of Shri. S.
    /// Surendra and Shri. G. Hari Hara Rao." — requires the literal word "Personal" immediately before
    /// "Guarantee" so "Corporate Guarantee"/"Counter Guarantee"/"Bank Guarantee limit of Rs..." (all real,
    /// verified false-positive shapes) never match.</summary>
    private static List<string> ExtractPersonalGuarantors(string? propertyParticulars)
    {
        if (string.IsNullOrWhiteSpace(propertyParticulars)) return [];

        var match = PersonalGuaranteeRegex().Match(propertyParticulars);
        if (!match.Success) return [];

        // Capture to end-of-string, not to the next period — the clause itself is full of periods from
        // title/initial abbreviations ("Shri.", "S."), so a "stop at the first period" capture (an earlier,
        // wrong version of this regex) truncated right after "Personal Guarantee of Shri" every time.
        var clause = match.Groups[1].Value.Trim().TrimEnd('.');

        return clause
            .Split([" and ", ","], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(StripTitles)
            .Where(n => n.Length > 0 && !DesignationPhrases.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    [GeneratedRegex(@"personal\s+guarantee\s+of\s+(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex PersonalGuaranteeRegex();

    // ── Check 3: charge holder ↔ requesting client ─────────────────────────────────────────────────

    private static RuleEvaluationOutcome EvaluateChargeHolderVsClient(AnalysisContext ctx)
    {
        if (ctx.Charges.Count == 0)
            return RuleEvaluationOutcome.NotEvaluated(ChargeHolderIsRequestingClientCode, "No charge records available.");
        if (string.IsNullOrWhiteSpace(ctx.Request.Client?.ClientName))
            return RuleEvaluationOutcome.NotEvaluated(ChargeHolderIsRequestingClientCode, "Requesting client is not on file for this request.");

        var normClient = NormalizeCompanyName(ctx.Request.Client.ClientName);
        if (normClient.Length < 4)
            return RuleEvaluationOutcome.NotEvaluated(ChargeHolderIsRequestingClientCode, "Requesting client name too short to match reliably.");

        // Same reasoning as Check 1: a satisfied charge is historical exposure, not a current holding —
        // "already holds a charge" must not be claimed about a charge repaid years ago.
        var matchingCharges = ctx.Charges
            .Where(c => c.SatisfactionDate is null
                && !string.IsNullOrWhiteSpace(c.LatestChargeHolderRaw) && NormalizeCompanyName(c.LatestChargeHolderRaw) == normClient)
            .ToList();

        if (matchingCharges.Count == 0)
            return RuleEvaluationOutcome.NotTriggered();

        return RuleEvaluationOutcome.Triggered(new FindingDraft(
            FindingSection.Charges, FindingSeverity.Watch, TemporalStatus.Current,
            ChargeHolderIsRequestingClientCode, "Requesting Client Already Holds a Charge on This Company",
            $"{ctx.Request.Client.ClientName}, which requested this dossier, is already a charge holder on this company ({matchingCharges.Count} charge(s)).",
            MetricsJson: JsonSerializer.Serialize(new
            {
                clientName = ctx.Request.Client.ClientName,
                chargeNumbers = matchingCharges.Select(c => c.RocChargeNumber)
            }),
            SourceReferenceJson: JsonSerializer.Serialize(new
            {
                entityType = nameof(RocCharge),
                entityIds = matchingCharges.Select(c => c.ChargeId).OrderBy(id => id).ToArray()
            })));
    }

    // ── Shared helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The only litigation rows worth cross-referencing as a *current* adverse/informational
    /// signal: MatchStatus=Probable/Uncertain rows aren't even vendor-confirmed to belong to this company
    /// (LitigationRules never treats them as a confirmed adverse signal either), and a resolved case
    /// (LitigationRules.IsPending's own closed-keyword definition — disposed/closed/dismissed/withdrawn/
    /// settled) describes historical exposure, not something currently true. Excluded rows are simply not
    /// considered — matching LitigationRules.Evaluate's own precedent of filtering to pending cases up
    /// front rather than emitting a separate Historical finding for resolved ones.</summary>
    private static IEnumerable<Litigation> ConfirmedPendingLitigations(AnalysisContext ctx) =>
        ctx.Litigations.Where(l => l.MatchStatus == LitigationMatchStatus.Confirmed && LitigationRules.IsPending(l.CaseStatus));

    private static string LitigationRef(IEnumerable<Litigation> cases) => JsonSerializer.Serialize(new
    {
        entityType = nameof(Litigation),
        entityIds = cases.Select(l => l.LitigationId).OrderBy(id => id).ToArray()
    });

    /// <summary>Uppercase, collapse whitespace, strip trailing punctuation, strip a small set of common
    /// corporate suffixes — goes further than NameNormalizer.Normalize() on purpose (that one is used for
    /// charge-holder grouping/HHI concentration metrics elsewhere and must not change), so the same lender
    /// filed as "ADITYA BIRLA FINANCE LIMITED" in one place and "ADITYA BIRLA FINANCE LTD." in another still
    /// matches here.</summary>
    private static string NormalizeCompanyName(string raw)
    {
        var collapsed = string.Join(' ', raw.Trim().Split([' ', '.', ','], StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        var tokens = collapsed.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !CorporateSuffixes.Contains(t))
            .ToList();
        return string.Join(' ', tokens);
    }

    private static string StripTitles(string raw)
    {
        var tokens = raw.Trim().Split([' ', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !PersonTitles.Contains(t.ToUpperInvariant()))
            .ToList();
        return string.Join(' ', tokens);
    }

    private static HashSet<string> Tokenize(string raw) =>
        raw.ToUpperInvariant().Split([' ', '.', ',', '-', '(', ')'], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    /// <summary>Matches only on a person name's single longest (most distinctive) token — deliberately not
    /// "any shared token" — so a common short name component never triggers a false personal-litigation
    /// claim. A name whose longest token is under 4 characters is treated as too short to match reliably
    /// and never triggers.</summary>
    private static bool MatchesOnDistinctiveToken(string personName, HashSet<string> targetTokens)
    {
        var tokens = personName.ToUpperInvariant().Split([' ', '.', ','], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !PersonTitles.Contains(t))
            .ToList();
        if (tokens.Count == 0) return false;

        var longest = tokens.OrderByDescending(t => t.Length).First();
        return longest.Length >= 4 && targetTokens.Contains(longest);
    }
}
