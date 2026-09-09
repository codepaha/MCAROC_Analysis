using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.McaFilings;

public record ParsedFilingIdentity(string? Srn, string? CompanyName, string? Cin);

/// <summary>Parses "{SRN}_{COMPANY}_{CIN}.zip" nested-zip filenames, e.g.
/// "70908_COASTAL_PROJECTS_U45203OR1995PLC003982.zip" → SRN 70908, Company "COASTAL PROJECTS",
/// CIN "U45203OR1995PLC003982". A CIN is a fixed 21-character pattern (letter, 5 digits, 2-letter state,
/// 4-digit year, 3-letter type, 6-digit number), which is used as the anchor to split the filename
/// reliably even though the company name portion itself contains underscores.</summary>
public static partial class FilingIdentityParser
{
    [GeneratedRegex(@"^(\d+)_(.+)_([A-Z]{1}\d{5}[A-Z]{2}\d{4}[A-Z]{3}\d{6})$", RegexOptions.IgnoreCase)]
    private static partial Regex NestedZipNamePattern();

    public static ParsedFilingIdentity Parse(string nestedZipFileNameWithoutExtension)
    {
        var match = NestedZipNamePattern().Match(nestedZipFileNameWithoutExtension);
        if (!match.Success)
            return new ParsedFilingIdentity(null, null, null);

        var srn = match.Groups[1].Value;
        var company = match.Groups[2].Value.Replace('_', ' ').Trim();
        var cin = match.Groups[3].Value.ToUpperInvariant();
        return new ParsedFilingIdentity(srn, company, cin);
    }
}
