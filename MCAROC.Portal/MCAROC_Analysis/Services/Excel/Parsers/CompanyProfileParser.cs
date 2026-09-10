using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses "About the Company" — a flat label/value sheet with section headers (e.g. "REGISTERED
/// ADDRESS:") that repeat labels like "Address Line 1" for different address blocks, so this tracks the
/// current section while scanning rather than treating it as a simple dictionary. The Email block is
/// special: the first address sits on the "Email" row, further addresses on their own label-less rows,
/// and a "* Our attempt to reach this email was not successful." footnote marks the "*"-suffixed one.</summary>
public static class CompanyProfileParser
{
    private const string ParserName = nameof(CompanyProfileParser);

    public static ParseResult<CompanyProfile> Parse(SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId) =>
        Parse(sheet, requestId, ingestionRunId, sourceDocumentId, out _);

    public static ParseResult<CompanyProfile> Parse(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId, out List<CompanyEmail> emails)
    {
        var result = new ParseResult<CompanyProfile>();
        emails = [];

        var profile = new CompanyProfile
        {
            RequestId = requestId,
            IngestionRunId = ingestionRunId,
            SourceDocumentId = sourceDocumentId,
            SourceSheetName = sheet.Name,
            SourceRowNumber = 1
        };

        string? currentSection = null;
        string? regLine1 = null, regLine2 = null;
        string? bizLine1 = null, bizLine2 = null, bizCity = null, bizState = null, bizPin = null;
        var rawEmails = new List<(string Raw, int SourceRow)>();
        var anyEmailUnreachable = false;
        var inEmailBlock = false;

        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var label = row.Count > 0 ? row[0]?.ToString()?.Trim() : null;
            var value = row.Count > 1 ? row[1] : null;
            var valueText = value?.ToString()?.Trim();

            // The Email block runs on for a few label-less rows: each further address, then a
            // "* Our attempt to reach this email was not successful." footnote — the address may be
            // in column 0 (older exports) or column 1 (current).
            if (string.IsNullOrEmpty(label) && inEmailBlock)
            {
                if (!string.IsNullOrWhiteSpace(valueText))
                {
                    if (valueText.Contains('@')) rawEmails.Add((valueText, r + 1));
                    else if (valueText.StartsWith('*') && valueText.Contains("not successful", StringComparison.OrdinalIgnoreCase)) anyEmailUnreachable = true;
                    else inEmailBlock = false;
                }
                continue;
            }

            if (string.IsNullOrEmpty(label))
            {
                inEmailBlock = false;
                continue;
            }
            inEmailBlock = false;

            if (label.EndsWith(':') && string.IsNullOrEmpty(valueText))
            {
                currentSection = label.TrimEnd(':').Trim().ToUpperInvariant();
                continue;
            }

            // Column-0 email continuation (older export shape).
            if (label.Contains('@'))
            {
                rawEmails.Add((label, r + 1));
                inEmailBlock = true;
                continue;
            }
            if (label.StartsWith('*') && label.Contains("not successful", StringComparison.OrdinalIgnoreCase))
            {
                anyEmailUnreachable = true;
                continue;
            }

            switch (label)
            {
                case "Legal Name": profile.CompanyName = valueText ?? string.Empty; break;
                case "CIN": profile.Cin = valueText; break;
                case "PAN": profile.Pan = valueText; break;

                // The About sheet's header carries source-export timestamp rows (report generated,
                // data-refresh, MCA-master-data-updated, documents-collected). We deliberately do NOT
                // retain these — they describe the upstream export, not the company.

                case "Authorised Capital (Crore)":
                    if (AmountNormalizer.TryParse(value, out var authCap, out _)) profile.AuthorisedCapital = authCap;
                    break;
                case "Paid Up Capital (Crore)":
                    if (AmountNormalizer.TryParse(value, out var paidCap, out _)) profile.PaidUpCapital = paidCap;
                    break;
                case "Sum of Charges (Crore)":
                    if (AmountNormalizer.TryParse(value, out var soc, out _)) profile.McaSumOfChargesCrore = soc;
                    break;

                case "Company Status": profile.CompanyStatus = valueText; break;
                case "Active Compliance": profile.ComplianceStatus = valueText; break;

                case "Date of Incorporation":
                    if (DateNormalizer.TryParse(value, out var incDate)) profile.IncorporationDate = incDate;
                    else result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, nameof(profile.IncorporationDate),
                        valueText, "BAD_DATE", $"Could not parse incorporation date '{valueText}'", r + 1));
                    break;
                case "Date of Last AGM":
                    if (DateNormalizer.TryParse(value, out var agmDate)) profile.LastAgmDate = agmDate;
                    break;

                case "Website": profile.Website = Nullify(valueText); break;
                case "Phone": profile.Phone = Nullify(valueText); break;
                case "Email":
                    if (!string.IsNullOrWhiteSpace(valueText)) rawEmails.Add((valueText, r + 1));
                    inEmailBlock = true;
                    break;

                case "Type of Entity": profile.EntityType = Nullify(valueText); break;
                case "Listing Status": profile.ListingStatus = Nullify(valueText); break;
                case "LEI":
                    (profile.Lei, profile.LeiStatus) = SplitLei(valueText);
                    break;

                case "Industry": profile.Industry = Nullify(valueText); break;
                case "Segment(s)": profile.Segment = Nullify(valueText); break;
                case "About the Company": profile.NarrativeDescription = Nullify(valueText); break;

                case "Address Line 1" when currentSection == "REGISTERED ADDRESS": regLine1 = valueText; break;
                case "Address Line 2" when currentSection == "REGISTERED ADDRESS": regLine2 = valueText; break;
                case "City" when currentSection == "REGISTERED ADDRESS": profile.RegisteredAddressCity = Nullify(valueText); break;
                case "State" when currentSection == "REGISTERED ADDRESS": profile.RegisteredAddressState = Nullify(valueText); break;
                case "Pin Code" when currentSection == "REGISTERED ADDRESS": profile.RegisteredAddressPinCode = Nullify(valueText); break;

                case "Address Line 1" when currentSection == "BUSINESS ADDRESS": bizLine1 = valueText; break;
                case "Address Line 2" when currentSection == "BUSINESS ADDRESS": bizLine2 = valueText; break;
                case "City" when currentSection == "BUSINESS ADDRESS": bizCity = valueText; break;
                case "State" when currentSection == "BUSINESS ADDRESS": bizState = valueText; break;
                case "Pin Code" when currentSection == "BUSINESS ADDRESS": bizPin = valueText; break;
            }
        }

        profile.RegisteredAddress = Join(regLine1, regLine2, profile.RegisteredAddressCity, profile.RegisteredAddressState, profile.RegisteredAddressPinCode);
        profile.BusinessAddress = Join(bizLine1, bizLine2, bizCity, bizState, bizPin);

        // Dedup by address (case-insensitive), keeping the first source row each address appears on —
        // that row number is retained on the entity for BFSI lineage back to the workbook.
        var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawValue, sourceRow) in rawEmails)
        {
            var raw = rawValue.Trim();
            if (raw.Length == 0) continue;
            var marked = raw.EndsWith('*');
            var address = raw.TrimEnd('*', ' ');
            if (address.Length == 0 || !seenAddresses.Add(address)) continue;
            emails.Add(new CompanyEmail
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = sourceRow,
                EmailAddress = address,
                // A "*"-suffixed address is the one the footnote is about; if there is exactly one
                // address and the footnote is present, it applies to that one.
                IsReachable = (marked && anyEmailUnreachable) ? false
                    : (rawEmails.Count == 1 && anyEmailUnreachable) ? false
                    : null
            });
        }

        if (string.IsNullOrEmpty(profile.CompanyName) || string.IsNullOrEmpty(profile.Cin))
        {
            result.AddError(new ParseIssue(IssueSeverity.Error, ParserName, null, null,
                "MISSING_REQUIRED_FIELD", "Company Name or CIN missing from About the Company sheet"));
        }

        result.Items.Add(profile);
        return result;
    }

    private static string? Nullify(string? text) =>
        string.IsNullOrWhiteSpace(text) || text is "-" or "NA" ? null : text;

    private static string? Join(params string?[] parts)
    {
        var joined = string.Join(", ", parts.Select(p => p?.Trim()).Where(p => !string.IsNullOrWhiteSpace(p) && p != "-"));
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>"335800S8JDNSIUXUSS97 ( ISSUED )" → ("335800S8JDNSIUXUSS97", "ISSUED").</summary>
    private static (string?, string?) SplitLei(string? text)
    {
        var t = Nullify(text);
        if (t is null) return (null, null);
        var open = t.IndexOf('(');
        if (open < 0) return (t.Trim(), null);
        var status = t[(open + 1)..].TrimEnd(')', ' ').Trim();
        return (t[..open].Trim(), status.Length == 0 ? null : status);
    }
}
