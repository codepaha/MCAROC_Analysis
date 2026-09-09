namespace MCAROC_Analysis.Services.Analysis;

public enum NormalizedComplianceStatus
{
    Compliant,
    NonCompliant,
    Unknown
}

/// <summary>Must check "non-compliant" before "compliant" — "Non-Compliant" contains "Compliant" as a
/// literal substring, so a bare .Contains("compliant") check alone would misclassify it as Compliant. This
/// is exactly that guard, not a single substring test.</summary>
public static class ComplianceStatusNormalizer
{
    public static NormalizedComplianceStatus Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return NormalizedComplianceStatus.Unknown;
        var text = raw.Trim().ToLowerInvariant();
        if (text.Contains("non-compliant", StringComparison.Ordinal)
            || text.Contains("non compliant", StringComparison.Ordinal)
            || text.Contains("noncompliant", StringComparison.Ordinal))
            return NormalizedComplianceStatus.NonCompliant;
        if (text.Contains("compliant", StringComparison.Ordinal))
            return NormalizedComplianceStatus.Compliant;
        return NormalizedComplianceStatus.Unknown;
    }
}
