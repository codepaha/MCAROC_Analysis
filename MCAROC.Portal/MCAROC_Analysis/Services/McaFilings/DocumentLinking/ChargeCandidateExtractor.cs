using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.McaFilings.DocumentLinking;

/// <summary>
/// Extracted candidate metadata from a Coastal corpus manifest entry.
/// </summary>
public sealed record ExtractedCandidate
{
    public required FilingCategory Category { get; init; }
    public string? FormType { get; init; }
    public ClassificationConfidence Confidence { get; init; }
    public required string ClassificationMethod { get; init; }
    public string? ClassificationRule { get; init; }
    public string? ExtractedChargeId { get; init; }
    public DateOnly? FilingDate { get; init; }
    public DateOnly? EventDate { get; init; }
    public ChargeEventType? InferredEventType { get; init; }
    public bool HasMalformedDate { get; init; }
    public string? MalformedDateRawToken { get; init; }
}

/// <summary>
/// Pure, deterministic extractor of filing candidate metadata from a manifest entry and filename.
/// </summary>
public static class ChargeCandidateExtractor
{
    private static readonly Regex ChargeIdRegex = new(@"(?i)ChargeId-(\d+)", RegexOptions.Compiled);

    public static ExtractedCandidate Extract(CoastalManifestEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var outerDir = Path.GetDirectoryName(entry.OuterEntryFullPath.Replace('\\', '/'));
        var outerCategoryFolder = !string.IsNullOrEmpty(outerDir) ? Path.GetFileName(outerDir) : string.Empty;
        var nestedDir = Path.GetDirectoryName(entry.NestedEntryRelativePath.Replace('\\', '/'));
        var sourceFolder = !string.IsNullOrEmpty(nestedDir) ? Path.GetFileName(nestedDir) : string.Empty;
        var originalFileName = Path.GetFileName(entry.NestedEntryRelativePath);

        var classification = FilingClassifier.Classify(
            outerCategoryFolder: outerCategoryFolder,
            sourceFolder: sourceFolder,
            originalFileName: originalFileName,
            firstPageText: null);

        var chargeIdMatch = ChargeIdRegex.Match(originalFileName);
        string? extractedChargeId = chargeIdMatch.Success ? chargeIdMatch.Groups[1].Value : null;

        DateOnly? filingDate = null;
        DateOnly? eventDate = null;
        bool hasMalformedDate = false;
        string? malformedDateToken = null;
        ChargeEventType? inferredEventType = null;

        if (chargeIdMatch.Success)
        {
            var prefix = originalFileName[..chargeIdMatch.Index].TrimEnd('-');
            var parts = prefix.Split('-', StringSplitOptions.RemoveEmptyEntries);

            var candidateTokens = new List<string>();
            for (int i = parts.Length - 1; i >= 0 && candidateTokens.Count < 2; i--)
            {
                var p = parts[i].Trim();
                if (p.Equals("1", StringComparison.OrdinalIgnoreCase) && i > 0 && parts[i - 1].Contains("CHG", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                if (p.StartsWith("Form", StringComparison.OrdinalIgnoreCase) || p.Contains("CHG", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                candidateTokens.Insert(0, p);
            }

            // Inferred event type: Form 17 is strictly Satisfaction; Form 8 / CHG-1 can be Creation or Modification (leave null)
            var nameLower = originalFileName.ToLowerInvariant();
            if (nameLower.Contains("form 17") || nameLower.Contains("form-17"))
            {
                inferredEventType = ChargeEventType.Satisfaction;
            }

            // Parse date tokens: Token 1 is FilingDate, Token 2 is EventDate
            var parsedDates = new List<DateOnly?>();
            foreach (var tok in candidateTokens)
            {
                if (TryParseDateToken(tok, out var dt))
                {
                    parsedDates.Add(dt);
                }
                else
                {
                    hasMalformedDate = true;
                    malformedDateToken = tok;
                    parsedDates.Add(null);
                }
            }

            if (!hasMalformedDate)
            {
                if (parsedDates.Count == 1)
                {
                    filingDate = parsedDates[0];
                }
                else if (parsedDates.Count >= 2)
                {
                    filingDate = parsedDates[0];
                    eventDate = parsedDates[1];
                }
            }
        }

        return new ExtractedCandidate
        {
            Category = classification.Category,
            FormType = classification.FormType,
            Confidence = classification.Confidence,
            ClassificationMethod = classification.Method,
            ClassificationRule = classification.MatchedRule,
            ExtractedChargeId = extractedChargeId,
            FilingDate = filingDate,
            EventDate = eventDate,
            InferredEventType = inferredEventType,
            HasMalformedDate = hasMalformedDate,
            MalformedDateRawToken = malformedDateToken
        };
    }

    private static bool TryParseDateToken(string token, out DateOnly date)
    {
        date = default;
        var trimmed = token.Trim();

        // 6-digit format: ddMMyy
        if (trimmed.Length == 6 &&
            int.TryParse(trimmed.AsSpan(0, 2), out int d2) &&
            int.TryParse(trimmed.AsSpan(2, 2), out int m2) &&
            int.TryParse(trimmed.AsSpan(4, 2), out int y2))
        {
            int yyyy = y2 <= 50 ? 2000 + y2 : 1900 + y2;
            try
            {
                date = new DateOnly(yyyy, m2, d2);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        // 8-digit format: ddMMyyyy
        if (trimmed.Length == 8 &&
            int.TryParse(trimmed.AsSpan(0, 2), out int d4) &&
            int.TryParse(trimmed.AsSpan(2, 2), out int m4) &&
            int.TryParse(trimmed.AsSpan(4, 4), out int y4))
        {
            try
            {
                date = new DateOnly(y4, m4, d4);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        return false;
    }
}
