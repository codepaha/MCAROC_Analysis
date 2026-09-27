using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.CompanyMaster;

/// <summary>Optional facts the requester knows that separate same-name companies (plan §5A.2 step 3). PAN is
/// accepted for the audit trail but not scored: the master data carries no PAN to compare against.</summary>
public sealed record ResolutionHints(
    string? State = null,
    string? District = null,
    string? PinCode = null,
    int? IncorporationYear = null,
    bool? Listed = null,
    EntityType? EntityType = null,
    string? Pan = null)
{
    public static readonly ResolutionHints None = new();
}

/// <summary>One master row as retrieved for ranking. <see cref="IsToolOnly"/> marks a hit that came only from the
/// reference tool's live search (not in our master) — such a hit is never auto-selected.</summary>
public sealed record MasterCandidate(
    string Identifier,
    CompanyMasterRecordType RecordType,
    string Name,
    string? NameNormalized = null,
    string? NameCore = null,
    EntityForm? EntityForm = null,
    string? Status = null,
    string? State = null,
    string? District = null,
    string? PinCode = null,
    DateOnly? RegistrationDate = null,
    string? Category = null,
    string? Class = null,
    string? ListingStatus = null,
    bool IsToolOnly = false);

/// <summary>A ranked candidate with its score and the per-feature breakdown that produced it — persisted so a
/// reviewer can see <i>why</i> it ranked where it did, and so a later algorithm version can be compared.</summary>
public sealed record ScoredCandidate(
    MasterCandidate Candidate,
    double Score,
    IReadOnlyDictionary<string, double> Features,
    IReadOnlyList<string> Reasons);

/// <summary>Tunable policy, bound from the <c>Resolve</c> configuration section. Everything that decides
/// <b>whether</b> to auto-select lives here, not in code, and is recorded with every resolution. Auto-select ships
/// off (<c>Resolve:AutoSelect:Enabled=false</c>); the thresholds are placeholders until the #293 harness report
/// sets them from data (plan §5A.4).</summary>
public sealed class ResolverOptions
{
    public const string SectionName = "Resolve";

    public bool AutoSelectEnabled { get; set; }
    public double AutoSelectThreshold { get; set; } = 0.95;
    public double MinimumMargin { get; set; } = 0.05;
    /// <summary><c>Resolve:AllowSoleActive</c>: among exact-name twins, exactly one Active may be selected.</summary>
    public bool AllowSoleActive { get; set; }
    /// <summary>Below this top score the name counts as not found (candidates still returned as suggestions).</summary>
    public double SuggestionFloor { get; set; } = 0.5;
    public int MaxCandidates { get; set; } = 10;
    /// <summary><c>Resolve:SpendThreshold</c> (plan §5A.3 <c>T_spend</c>): an <c>AutoSelected</c> identity may drive an
    /// unattended spend (auto-unlock, auto litigation search) only at or above this score — stricter than
    /// <see cref="AutoSelectThreshold"/>. A lower-confidence auto-selection still proceeds through the free steps.</summary>
    public double SpendThreshold { get; set; } = 0.99;
}

public sealed record ResolutionDecision(
    ResolutionStatus Status,
    ResolutionMethod? Method,
    string? ChosenIdentifier,
    string? RecommendedIdentifier,
    bool AutoSelectEligible,
    double? TopScore,
    double? Margin,
    string ReasonCode,
    NormalizedCompanyName NormalizedInput,
    IReadOnlyList<ScoredCandidate> Candidates);

/// <summary>Stable reason codes, shared with the pipeline's needs-attention vocabulary (plan §6.1).</summary>
public static class ResolutionReasonCodes
{
    public const string UserProvidedIdentifier = "RESOLVED_USER_PROVIDED_CIN";
    public const string AutoSelected = "RESOLVED_AUTO_SELECTED";
    public const string SoleActive = "RESOLVED_SOLE_ACTIVE";
    public const string HumanSelected = "RESOLVED_HUMAN_SELECTED";
    public const string AutoSelectDisabled = "AUTO_SELECT_DISABLED";
    public const string NeedsConfirmation = "IDENTITY_NEEDS_CONFIRMATION";
    public const string ToolOnlyNeedsConfirmation = "TOOL_ONLY_NEEDS_CONFIRMATION";
    public const string Ambiguous = "IDENTITY_AMBIGUOUS";
    public const string NotFound = "IDENTITY_NOT_FOUND";
    public const string IdentifierInvalid = "IDENTIFIER_INVALID";
    public const string IdentifierNotInMaster = "IDENTIFIER_NOT_IN_MASTER";
    public const string NameMissing = "NAME_MISSING";
    public const string DuplicateRequest = "DUPLICATE_REQUEST";
    /// <summary>The request already carries a different identifier; a resolution never re-points it.</summary>
    public const string RequestAlreadyIdentified = "REQUEST_ALREADY_IDENTIFIED";
    /// <summary>The reference tool's free preview named a different company than an <c>AutoSelected</c>
    /// resolution (issue #295): recorded as a false accept, and the identity goes back to a human.</summary>
    public const string FalseAccept = "IDENTITY_FALSE_ACCEPT";
}

/// <summary>Name → CIN/LLPIN resolution, pure part (issue #294, plan §5A.2 steps 3–4): rank retrieved master
/// candidates and decide whether one can be chosen. No I/O — retrieval is <see cref="CompanyNameCandidateRetriever"/>,
/// persistence is <c>IdentityResolutionService</c> — so every rule here is table-tested and reproducible from
/// the stored inputs under a given <see cref="AlgorithmVersion"/>.
///
/// The governing asymmetry: a wrong auto-selection buys the wrong company's data and produces a convincing
/// wrong-company dossier, while an unnecessary question to a human costs a click. So the decision leans hard
/// towards "ask": exact-name twins are always ambiguous without a distinguishing hint, a tool-only hit always
/// needs confirmation, and nothing is auto-selected unless it is an exact normalized match or hint-confirmed,
/// clears the threshold, and clears the runner-up by the margin — and auto-select is off by default.</summary>
public static partial class CompanyNameResolver
{
    /// <summary>Bump whenever scoring or decision output can change for the same inputs.</summary>
    public const int AlgorithmVersion = 1;

    private const double HintAgreeBonus = 0.02;
    private const double HintDisagreePenalty = 0.15;
    private const double FormConflictPenalty = 0.15;
    private const double InactivePenalty = 0.10;
    private const double UnknownStatusPenalty = 0.02;

    [GeneratedRegex("^(?:[LU][0-9]{5}[A-Z]{2}[0-9]{4}[A-Z]{3}[0-9]{6}|[A-Z]{3}-[0-9]{4})$")]
    private static partial Regex IdentifierPattern();

    /// <summary>CIN or LLPIN shape — the same pattern AutoFetch accepts.</summary>
    public static bool IsValidIdentifier(string? identifier) =>
        identifier is not null && IdentifierPattern().IsMatch(identifier.Trim().ToUpperInvariant());

    /// <summary>A supplied identifier short-circuits ranking: pattern-validate it and confirm the master knows it
    /// (plan §5A.2). A valid identifier absent from the master is not resolved — the master may be stale, but it
    /// may equally be a typo, and a human should look.</summary>
    public static ResolutionDecision ForProvidedIdentifier(string identifier, MasterCandidate? masterRow)
    {
        var id = identifier.Trim().ToUpperInvariant();
        var normalized = CompanyNameNormalizer.Normalize(masterRow?.Name);
        if (!IsValidIdentifier(id))
            return new(ResolutionStatus.InvalidInput, null, null, null, false, null, null, ResolutionReasonCodes.IdentifierInvalid, normalized, []);
        if (masterRow is null)
            return new(ResolutionStatus.NotFound, null, null, null, false, null, null, ResolutionReasonCodes.IdentifierNotInMaster, normalized, []);

        var scored = new ScoredCandidate(masterRow, 1.0, new Dictionary<string, double> { ["UserProvidedIdentifier"] = 1 }, ["identifier supplied by the requester"]);
        return new(ResolutionStatus.Resolved, ResolutionMethod.UserProvidedCin, masterRow.Identifier, masterRow.Identifier,
            false, 1.0, null, ResolutionReasonCodes.UserProvidedIdentifier, normalized, [scored]);
    }

    /// <summary>Scores every candidate against the input name and hints, best first. Ties are broken by
    /// identifier so the order — and therefore the stored top-N — is deterministic.</summary>
    public static IReadOnlyList<ScoredCandidate> Rank(string inputName, ResolutionHints hints, IEnumerable<MasterCandidate> candidates)
    {
        var input = CompanyNameNormalizer.Normalize(inputName);
        return candidates
            .GroupBy(c => c.Identifier, StringComparer.Ordinal).Select(g => g.First())
            .Select(c => Score(input, hints, c))
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Candidate.Identifier, StringComparer.Ordinal)
            .ToList();
    }

    public static ResolutionDecision Decide(string inputName, IReadOnlyList<ScoredCandidate> ranked, ResolverOptions options)
    {
        var input = CompanyNameNormalizer.Normalize(inputName);
        var shown = ranked.Take(options.MaxCandidates).ToList();

        if (input.NameNormalized.Length == 0)
            return new(ResolutionStatus.InvalidInput, null, null, null, false, null, null, ResolutionReasonCodes.NameMissing, input, []);
        if (ranked.Count == 0 || ranked[0].Score < options.SuggestionFloor)
            return new(ResolutionStatus.NotFound, null, null, null, false, ranked.FirstOrDefault()?.Score, null, ResolutionReasonCodes.NotFound, input, shown);

        var top = ranked[0];
        var margin = top.Score - (ranked.Count > 1 ? ranked[1].Score : 0);

        // Exact-name twins: the score can separate them only through hints or status, and status alone is not
        // enough — "struck off" twins are exactly the case where the requester may mean the struck-off one.
        var exactGroup = ranked.Where(r => r.Features["ExactNormalized"] == 1).ToList();
        var topIsExact = top.Features["ExactNormalized"] == 1;
        var soleActive = false;
        if (topIsExact && exactGroup.Count > 1 && !HintDistinguishes(top, exactGroup))
        {
            var active = exactGroup.Where(r => IsActive(r.Candidate.Status)).ToList();
            soleActive = options.AllowSoleActive && active.Count == 1 && active[0] == top;
            if (!soleActive)
                return new(ResolutionStatus.Ambiguous, null, null, null, false, top.Score, margin, ResolutionReasonCodes.Ambiguous, input, shown);
        }

        var hintConfirmed = top.Features.GetValueOrDefault("HintsAgreed") >= 1 && top.Features.GetValueOrDefault("HintsDisagreed") == 0;
        var clearsBar = top.Score >= options.AutoSelectThreshold && (margin >= options.MinimumMargin || soleActive);
        var eligible = clearsBar && (topIsExact || hintConfirmed) && !top.Candidate.IsToolOnly;

        if (!eligible)
        {
            // One candidate clearly ahead of the rest (by the margin) is a suggestion for a human to confirm,
            // not an ambiguity — even when it can never be auto-selected (a word-order or typo match, or a
            // tool-only hit). Ambiguous is reserved for candidates a human genuinely has to choose between.
            if (margin >= options.MinimumMargin)
                return new(ResolutionStatus.NeedsConfirmation, null, null, top.Candidate.Identifier, false, top.Score, margin,
                    top.Candidate.IsToolOnly ? ResolutionReasonCodes.ToolOnlyNeedsConfirmation : ResolutionReasonCodes.NeedsConfirmation, input, shown);
            return new(ResolutionStatus.Ambiguous, null, null, null, false, top.Score, margin, ResolutionReasonCodes.Ambiguous, input, shown);
        }

        if (!options.AutoSelectEnabled)
            return new(ResolutionStatus.NeedsConfirmation, null, null, top.Candidate.Identifier, true, top.Score, margin, ResolutionReasonCodes.AutoSelectDisabled, input, shown);

        return new(ResolutionStatus.Resolved, ResolutionMethod.AutoSelected, top.Candidate.Identifier, top.Candidate.Identifier, true,
            top.Score, margin, soleActive ? ResolutionReasonCodes.SoleActive : ResolutionReasonCodes.AutoSelected, input, shown);
    }

    /// <summary>Master status "Active" exactly — the same classification the registry dashboard uses; every
    /// other status (Strike Off, Amalgamated, Under CIRP, Under Liquidation, …) is inactive.</summary>
    public static bool IsActive(string? status) => string.Equals(status?.Trim(), "Active", StringComparison.OrdinalIgnoreCase);

    private static ScoredCandidate Score(NormalizedCompanyName input, ResolutionHints hints, MasterCandidate c)
    {
        var features = new Dictionary<string, double>(StringComparer.Ordinal);
        var reasons = new List<string>();

        var candidateNorm = c.NameNormalized is not null && c.NameCore is not null && c.EntityForm is not null
            ? new NormalizedCompanyName(c.NameNormalized, c.NameCore, c.EntityForm.Value)
            : CompanyNameNormalizer.Normalize(c.Name);

        var exact = input.NameNormalized.Length > 0 && candidateNorm.NameNormalized == input.NameNormalized;
        var coreSimilarity = CoreSimilarity(input.NameCore, candidateNorm.NameCore);
        features["ExactNormalized"] = exact ? 1 : 0;
        features["CoreSimilarity"] = coreSimilarity;
        var score = exact ? 1.0 : 0.9 * coreSimilarity;
        reasons.Add(exact ? "exact normalized name" : $"name similarity {coreSimilarity:P0}");

        var formMatch = input.EntityForm == EntityForm.Unknown || candidateNorm.EntityForm == EntityForm.Unknown ? 0.5
            : input.EntityForm == candidateNorm.EntityForm ? 1.0 : 0.0;
        features["EntityFormMatch"] = formMatch;
        if (formMatch == 0)
        {
            score -= FormConflictPenalty;
            reasons.Add($"legal form differs ({candidateNorm.EntityForm} vs requested {input.EntityForm})");
        }

        if (IsActive(c.Status)) features["Active"] = 1;
        else if (string.IsNullOrWhiteSpace(c.Status)) { features["Active"] = 0.5; score -= UnknownStatusPenalty; }
        else
        {
            features["Active"] = 0;
            score -= InactivePenalty;
            reasons.Add($"status {c.Status.Trim()}");
        }

        var (agreed, disagreed) = ScoreHints(hints, c, reasons);
        features["HintsAgreed"] = agreed;
        features["HintsDisagreed"] = disagreed;
        score += agreed * HintAgreeBonus - disagreed * HintDisagreePenalty;

        if (c.IsToolOnly) reasons.Add("found only by the reference tool's live search");
        return new ScoredCandidate(c, Math.Round(Math.Clamp(score, 0, 1), 6), features, reasons);
    }

    private static (int Agreed, int Disagreed) ScoreHints(ResolutionHints hints, MasterCandidate c, List<string> reasons)
    {
        int agreed = 0, disagreed = 0;
        void Compare(string label, bool? same)
        {
            if (same is null) return;
            if (same.Value) { agreed++; reasons.Add($"{label} matches"); }
            else { disagreed++; reasons.Add($"{label} differs"); }
        }

        Compare("state", Both(hints.State, c.State, (a, b) => Fold(a) == Fold(b)));
        Compare("district", Both(hints.District, c.District, (a, b) => Fold(a) == Fold(b)));
        Compare("PIN code", Both(hints.PinCode, c.PinCode, (a, b) => Digits(a) == Digits(b)));
        Compare("incorporation year", hints.IncorporationYear is { } year && c.RegistrationDate is { } reg ? year == reg.Year : null);
        Compare("listing", hints.Listed is { } listed && ParseListed(c.ListingStatus) is { } isListed ? listed == isListed : null);
        Compare("entity type", hints.EntityType is { } type
            ? (type == EntityType.LLP) == (c.RecordType == CompanyMasterRecordType.Llp)
            : null);
        return (agreed, disagreed);
    }

    /// <summary>True when the top exact twin is set apart by hints: it agrees on more hints (net of disagreements)
    /// than every other twin. Without that, twins stay ambiguous whatever their scores.</summary>
    private static bool HintDistinguishes(ScoredCandidate top, IReadOnlyList<ScoredCandidate> twins)
    {
        static double Net(ScoredCandidate s) => s.Features.GetValueOrDefault("HintsAgreed") - s.Features.GetValueOrDefault("HintsDisagreed");
        return Net(top) > 0 && twins.Where(t => t != top).All(t => Net(t) < Net(top));
    }

    /// <summary>Order-insensitive similarity of two cores (Dice over greedily matched tokens). A token matches
    /// exactly (1), or within one edit when both are ≥ 5 letters (0.85), or two edits when both are ≥ 8
    /// (0.7) — enough for the common typo without matching unrelated short words. Cores that differ only by
    /// spacing ("FILM NAGAR" / "FILMNAGAR") score 0.95.</summary>
    public static double CoreSimilarity(string a, string b)
    {
        var left = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var right = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (left.Length == 0 || right.Length == 0) return 0;
        if (string.Concat(left) == string.Concat(right)) return left.Length == right.Length ? 1 : 0.95;

        var used = new bool[right.Length];
        double total = 0;
        foreach (var token in left.OrderByDescending(t => t.Length))
        {
            int bestIndex = -1;
            double best = 0;
            for (var j = 0; j < right.Length; j++)
            {
                if (used[j]) continue;
                var s = TokenSimilarity(token, right[j]);
                if (s > best) { best = s; bestIndex = j; }
            }
            if (bestIndex >= 0) { used[bestIndex] = true; total += best; }
        }
        return Math.Round(2 * total / (left.Length + right.Length), 6);
    }

    private static double TokenSimilarity(string a, string b)
    {
        if (a == b) return 1;
        var shorter = Math.Min(a.Length, b.Length);
        if (shorter < 5 || Math.Abs(a.Length - b.Length) > 2) return 0;
        var distance = EditDistance(a, b, maxDistance: 2);
        return distance == 1 ? 0.85 : distance == 2 && shorter >= 8 ? 0.7 : 0;
    }

    /// <summary>Optimal-string-alignment distance (Levenshtein plus adjacent transposition), capped: returns
    /// <paramref name="maxDistance"/> + 1 as soon as the distance must exceed it.</summary>
    public static int EditDistance(string a, string b, int maxDistance = int.MaxValue)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var rowMin = int.MaxValue;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    v = Math.Min(v, d[i - 2, j - 2] + 1);
                d[i, j] = v;
                rowMin = Math.Min(rowMin, v);
            }
            if (rowMin > maxDistance) return maxDistance + 1;
        }
        return d[a.Length, b.Length];
    }

    private static bool? Both(string? hint, string? value, Func<string, string, bool> same) =>
        string.IsNullOrWhiteSpace(hint) || string.IsNullOrWhiteSpace(value) ? null : same(hint, value);

    private static string Fold(string s) => CompanyNameNormalizer.Normalize(s).NameNormalized;
    private static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());

    private static bool? ParseListed(string? listingStatus)
    {
        if (string.IsNullOrWhiteSpace(listingStatus)) return null;
        var s = listingStatus.Trim().ToUpperInvariant();
        return s.Contains("UNLISTED", StringComparison.Ordinal) ? false : s.Contains("LISTED", StringComparison.Ordinal) ? true : null;
    }
}
