using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses "About the Company" — a flat label/value sheet with section headers (e.g. "REGISTERED
/// ADDRESS:") that repeat labels like "Address Line 1" for different address blocks, so this tracks the
/// current section while scanning rather than treating it as a simple dictionary.</summary>
public static class CompanyProfileParser
{
    private const string ParserName = nameof(CompanyProfileParser);

    public static ParseResult<CompanyProfile> Parse(SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var result = new ParseResult<CompanyProfile>();
        var profile = new CompanyProfile
        {
            RequestId = requestId,
            IngestionRunId = ingestionRunId,
            SourceDocumentId = sourceDocumentId,
            SourceSheetName = sheet.Name,
            SourceRowNumber = 1
        };

        string? currentSection = null;
        string? regLine1 = null, regLine2 = null, regCity = null, regState = null, regPin = null;

        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var label = row.Count > 0 ? row[0]?.ToString()?.Trim() : null;
            var value = row.Count > 1 ? row[1] : null;
            var valueText = value?.ToString()?.Trim();

            if (string.IsNullOrEmpty(label))
                continue;

            if (label.EndsWith(':') && string.IsNullOrEmpty(valueText))
            {
                currentSection = label.TrimEnd(':').Trim().ToUpperInvariant();
                continue;
            }

            switch (label)
            {
                case "Legal Name":
                    profile.CompanyName = valueText ?? string.Empty;
                    break;
                case "CIN":
                    profile.Cin = valueText;
                    break;
                case "PAN":
                    profile.Pan = valueText;
                    break;
                case "Authorised Capital (Crore)":
                    if (AmountNormalizer.TryParse(value, out var authCap, out _)) profile.AuthorisedCapital = authCap;
                    break;
                case "Paid Up Capital (Crore)":
                    if (AmountNormalizer.TryParse(value, out var paidCap, out _)) profile.PaidUpCapital = paidCap;
                    break;
                case "Company Status":
                    profile.CompanyStatus = valueText;
                    break;
                case "Active Compliance":
                    profile.ComplianceStatus = valueText;
                    break;
                case "Date of Incorporation":
                    if (DateNormalizer.TryParse(value, out var incDate))
                        profile.IncorporationDate = incDate;
                    else
                        result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, nameof(profile.IncorporationDate),
                            valueText, "BAD_DATE", $"Could not parse incorporation date '{valueText}'", r + 1));
                    break;
                case "Industry":
                    profile.Industry = valueText;
                    break;
                case "Type of Entity":
                    profile.BusinessActivity = valueText;
                    break;
                case "About the Company":
                    profile.BusinessActivity = string.IsNullOrEmpty(profile.BusinessActivity)
                        ? valueText
                        : $"{profile.BusinessActivity} — {valueText}";
                    break;
                case "Address Line 1" when currentSection == "REGISTERED ADDRESS":
                    regLine1 = valueText;
                    break;
                case "Address Line 2" when currentSection == "REGISTERED ADDRESS":
                    regLine2 = valueText;
                    break;
                case "City" when currentSection == "REGISTERED ADDRESS":
                    regCity = valueText;
                    break;
                case "State" when currentSection == "REGISTERED ADDRESS":
                    regState = valueText;
                    break;
                case "Pin Code" when currentSection == "REGISTERED ADDRESS":
                    regPin = valueText;
                    break;
            }
        }

        profile.RegisteredAddress = string.Join(", ",
            new[] { regLine1, regLine2, regCity, regState, regPin }.Where(s => !string.IsNullOrWhiteSpace(s)));

        if (string.IsNullOrEmpty(profile.CompanyName) || string.IsNullOrEmpty(profile.Cin))
        {
            result.AddError(new ParseIssue(IssueSeverity.Error, ParserName, null, null,
                "MISSING_REQUIRED_FIELD", "Company Name or CIN missing from About the Company sheet"));
        }

        result.Items.Add(profile);
        return result;
    }
}
