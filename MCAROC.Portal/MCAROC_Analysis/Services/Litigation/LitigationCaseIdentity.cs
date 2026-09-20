using System.Text;
using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>
/// Normalises identity evidence received from litigation providers without changing the source values.
///
/// This type intentionally has a very narrow automatic merge rule: only a shared, valid CNR and
/// compatible proceeding types can identify two provider observations as the same case. CSP is retained
/// as feed-link metadata, but is not an identity assertion: it may be absent and its scope has not yet
/// been established in the BPR contract.
/// </summary>
public static partial class LitigationCaseIdentity
{
    public static LitigationCaseIdentityEvidence Normalise(LitigationCaseIdentityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var cnr = NormaliseCnr(input.CnrNumber);
        var proceedingType = NormaliseProceedingType(input.CaseType);
        var caseNumber = ParseCaseNumber(input.CaseNumber, input.CaseYear, proceedingType);

        return new LitigationCaseIdentityEvidence(
            Cnr: cnr,
            ProceedingType: proceedingType,
            CaseNumber: caseNumber,
            CspId: NullIfEmpty(NormaliseDisplay(input.CspId)));
    }

    /// <summary>
    /// Returns true only for the evidence that the duplicate-case FSD treats as decisive. All other
    /// combinations stay as separate observations until the provider contract and review policy define
    /// a broader, auditable reconciliation path.
    /// </summary>
    public static bool CanAutoDedupe(
        LitigationCaseIdentityEvidence left,
        LitigationCaseIdentityEvidence right) =>
        left.Cnr is not null
        && string.Equals(left.Cnr, right.Cnr, StringComparison.Ordinal)
        && AreCompatibleProceedingTypes(left.ProceedingType, right.ProceedingType);

    public static string? NormaliseCnr(string? value)
    {
        var candidate = CnrSeparators().Replace(NormaliseDisplay(value), string.Empty).ToUpperInvariant();
        if (candidate.Length != 16 || !CnrFormat().IsMatch(candidate) || IsPlaceholder(candidate) || IsRepeated(candidate))
            return null;
        return candidate;
    }

    public static string? NormaliseProceedingType(string? value)
    {
        var candidate = NonAlphaNumeric().Replace(NormaliseDisplay(value), string.Empty).ToUpperInvariant();
        if (candidate.Length == 0 || IsPlaceholder(candidate)) return null;

        return candidate switch
        {
            "C" => "CC",
            "CC" => "CC",
            _ => candidate
        };
    }

    private static LitigationCaseNumber? ParseCaseNumber(string? value, int? caseYear, string? proceedingType)
    {
        var match = CaseNumberFormat().Match(NormaliseDisplay(value));
        if (!match.Success) return null;

        var serial = match.Groups["serial"].Value;
        var yearText = match.Groups["year"].Value;
        if (!int.TryParse(yearText, out var year) || caseYear is not null && caseYear != year) return null;

        var type = NormaliseProceedingType(match.Groups["type"].Value) ?? proceedingType;
        return type is null ? null : new LitigationCaseNumber(type, serial, year);
    }

    private static bool AreCompatibleProceedingTypes(string? left, string? right) =>
        left is not null && right is not null && string.Equals(left, right, StringComparison.Ordinal);

    private static bool IsPlaceholder(string value) => value is "" or "-" or "." or "NA" or "NIL" or "NULL" or "DUMMY" or "TEST";

    private static bool IsRepeated(string value) => value.Length > 1 && value.All(character => character == value[0]);

    private static string NormaliseDisplay(string? value)
    {
        var result = value is null ? string.Empty : Whitespace().Replace(value.Normalize(NormalizationForm.FormC).Trim(), " ");
        return result;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    [GeneratedRegex("[\\s-]+", RegexOptions.CultureInvariant)]
    private static partial Regex CnrSeparators();

    [GeneratedRegex("^[A-Z0-9]{12}\\d{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex CnrFormat();

    [GeneratedRegex("[^A-Za-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaNumeric();

    [GeneratedRegex("^(?:(?<type>[A-Za-z. ]+)\\s*)?(?<serial>\\d+)\\s*/\\s*(?<year>\\d{4})$", RegexOptions.CultureInvariant)]
    private static partial Regex CaseNumberFormat();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}

public sealed record LitigationCaseIdentityInput(
    string? CnrNumber,
    string? CaseNumber,
    int? CaseYear,
    string? CaseType,
    string? CspId = null);

public sealed record LitigationCaseIdentityEvidence(
    string? Cnr,
    string? ProceedingType,
    LitigationCaseNumber? CaseNumber,
    string? CspId);

public sealed record LitigationCaseNumber(string ProceedingType, string Serial, int Year);
