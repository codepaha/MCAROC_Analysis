using System.Text.RegularExpressions;
using MCAROC_Analysis.Models;

namespace MCAROC_Analysis.Services.PreLoginReports;

/// <summary>Parses Annexure I labels as data. Routing/process/vendor sections are deliberately excluded.
/// Identifiers are shape-validated, never guessed from OCR substitutions or source labels.</summary>
public static class BorrowerRequestParser
{
    private static Match Find(string text, string pattern) => Regex.Match(text, pattern,
        RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromSeconds(1));
    private static string? Clean(string? value)
    {
        var result = Regex.Replace(value ?? "", @"\s+", " ").Trim(' ', ':', '|', '_');
        return result.Length == 0 || Regex.IsMatch(result, @"^(?:N/?A|not applicable|nil|-+)$", RegexOptions.IgnoreCase) ? null : result;
    }
    private static string? Field(string text, string label, string? stop = null)
    {
        var match = Find(text, @"(?<![\p{L}\p{N}])(?:" + label + @")(?![\p{L}\p{N}])[ \t]*[:：\-]?[ \t]*([^\r\n]*)");
        var value = match.Success ? match.Groups[1].Value : null;
        if (value is not null && stop is not null)
        {
            var end = Find(value, stop);
            if (end.Success) value = value[..end.Index];
        }
        return Clean(value);
    }
    private static string? Code(string? text, string pattern)
    {
        var match = Find((text ?? "").ToUpperInvariant(), @"(?<![A-Z0-9])" + pattern + @"(?![A-Z0-9])");
        return match.Success ? match.Value : null;
    }
    private const string PanPattern = @"[A-Z]{5}[0-9]{4}[A-Z]";
    private const string CinPattern = @"[UL][0-9]{5}[A-Z]{2}[0-9]{4}[A-Z]{3}[0-9]{6}";

    public static BorrowerAssignmentDetails Parse(string text)
    {
        if (text.Length > 100_000) throw new PreLoginReportException("Request text exceeds 100,000 characters.");
        text = text.Replace('\a', '\n').Replace('\r', '\n').Replace('\u00a0', ' ');
        text = Regex.Replace(text, @"[\x00-\x08\x0B\x0C\x0E-\x1F]", "");
        // Bound the business section before contact/routing instructions can supply wrong emails/names.
        var administrative = Find(text, @"(?:Cubictree\s*\(To provide|Date of Receipt of Request|Cubictree Contact Details|^\s*Process\s*:)");
        if (administrative.Success) text = text[..administrative.Index];
        var split = Find(text, @"(?:Company|Borrower|Entity)\s+Details\s*:");
        var branchText = split.Success ? text[..split.Index] : text;
        var companyText = split.Success ? text[(split.Index + split.Length)..] : text;
        var result = new BorrowerAssignmentDetails();
        result.RequestDetails.DocumentTitle = Find(text, @"Request Form for Borrower Profiling Report").Success
            ? "Annexure I Request Form for Borrower Profiling Report" : null;
        result.RequestDetails.DateOfRequest = Field(branchText, @"Date\s+of\s+Request");
        var branch = result.RequestingBranchDetails;
        branch.BranchName = Field(branchText, @"Name\s+of\s+Req\s*uesting\s+Branch|Branch\s+Name");
        var branchCode = Find(branch.BranchName ?? "", @"\((\d{1,10})\)");
        branch.BranchCode = branchCode.Success ? branchCode.Groups[1].Value : Field(branchText, @"Branch\s+Code");
        if (branchCode.Success) branch.BranchName = Clean((branch.BranchName ?? "").Remove(branchCode.Index, branchCode.Length));
        branch.Address = Field(branchText, @"Requesting\s+Branch\s+Address|Branch\s+Address");
        var contact = branch.ContactPerson;
        var person = Field(branchText, @"Name\s+of\s+Person\s*&\s*Designation|Contact\s+Person");
        if (person is not null)
        {
            var designation = Find(person, @",\s*(.+)$|\s+((?:RM\s+SME|Relationship\s+Manager|Branch\s+Manager).*)$");
            contact.Name = Clean(designation.Success ? person[..designation.Index] : person);
            contact.Designation = designation.Success ? Clean(designation.Groups[1].Success ? designation.Groups[1].Value : designation.Groups[2].Value) : Field(branchText, @"^\s*Designation");
        }
        contact.PhoneLandline = Field(branchText, @"Contact\s+Number\s*\(Landline\)|Landline", @"Email\s*I[Dd]|Contact\s+Number");
        contact.PhoneMobile = Field(branchText, @"Contact\s+Number\s*\(Mobile\)|Mobile", @"Email\s*I[Dd]|\|");
        contact.Email = Code(Field(branchText, @"Email\s*I[Dd]|Email"), @"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}")?.ToLowerInvariant();
        var company = result.CompanyDetails;
        company.CompanyName = Field(companyText, @"Name\s+of\s+(?:the\s+)?(?:Company|Borrower|Trust|Firm|Entity)|(?:Company|Borrower|Trust|Firm)\s+Name");
        var declaredType = Field(companyText, @"Type\s+of\s+(?:Company|Entity)|Entity\s+Type");
        // A printed list of choices is not an actual entity-type selection.
        if (declaredType?.Contains('/') == true || declaredType?.StartsWith('(') == true) declaredType = null;
        company.EntityType = RecognizeType(declaredType) ?? RecognizeType(company.CompanyName) ?? declaredType;
        var identity = Field(companyText, @"CIN\s*&\s*Pan\s*No\.?|CIN(?:\s+No\.?)?|PAN(?:\s+No\.?)?", @"Incorporation\s+Date");
        company.Cin = Code(identity, CinPattern);
        company.Pan = Code(identity, PanPattern);
        company.Gstin = Code(Field(companyText, @"GST(?:IN|\s*No\.?)"), @"[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][A-Z0-9]Z[A-Z0-9]");
        company.IncorporationDate = Field(companyText, @"Incorporation\s+Date|Date\s+of\s+Incorporation");
        company.RegisteredOfficeAddress = Address(companyText, @"(?:Company\s+)?Registered\s+(?:office\s+)?Address|Registered\s+Office", @"Plant\s+Address|Factory\s+Address");
        company.PlantAddress = Address(companyText, @"Plant\s+Address|Factory\s+Address", null);
        var peopleHeader = Find(companyText, @"Name\s+of\s+(?:Directors?\s*/\s*Partners?|Partners?|Proprietors?|Trustees?).*");
        if (peopleHeader.Success)
        {
            var peopleText = companyText[(peopleHeader.Index + peopleHeader.Length)..];
            var address = Find(peopleText, @"(?:Company\s+)?Registered\s+(?:office\s+)?Address|Plant\s+Address|Factory\s+Address");
            if (address.Success) peopleText = peopleText[..address.Index];
            foreach (Match entry in Regex.Matches(peopleText, @"^[ \t]*\d+[ \t]*[.)][ \t]*(.*?)(?=^[ \t]*\d+[ \t]*[.)]|\z)", RegexOptions.Multiline | RegexOptions.Singleline, TimeSpan.FromSeconds(1)))
            {
                var line = Clean(entry.Groups[1].Value) ?? "";
                var pan = Code(line, PanPattern);
                var din = Code(line, @"[0-9]{8}");
                var separator = Find(line, @"\s*[,：:]\s*|\s+(?:PAN|DIN)\b|\s+[—–]\s*");
                var name = Clean(separator.Success ? line[..separator.Index] : Regex.Replace(line, @"\s+(?:[A-Z]{5}[0-9]{4}[A-Z]|[0-9]{8})\s*$", ""));
                if (name is not null) company.DirectorsOrPartners.Add(new() { Name = name, Pan = pan, Din = din });
                if (company.DirectorsOrPartners.Count == 30) break;
            }
        }
        return result;
    }

    private static string? Address(string text, string label, string? stop)
    {
        var start = Find(text, @"(?:" + label + @")\s*[:：\-]?\s*");
        if (!start.Success) return null;
        var value = text[(start.Index + start.Length)..];
        if (stop is not null)
        {
            var end = Find(value, stop);
            if (end.Success) value = value[..end.Index];
        }
        return Clean(Regex.Replace(value, @"^\s*Reg\s+Address\s*:\s*", "", RegexOptions.IgnoreCase));
    }

    public static string? RecognizeType(string? value)
    {
        var text = value ?? "";
        if (Find(text, @"\b(?:LLP|Limited Liability Partnership)\b").Success) return "LLP";
        if (Find(text, @"\bForeign\s+Company\b").Success) return "Foreign Company";
        if (Find(text, @"\b(?:Private\s+Limited|Pvt\.?\s*Ltd\.?)\b").Success) return "Private Limited";
        if (Find(text, @"\b(?:Limited|Ltd)\b").Success) return "Limited";
        if (Find(text, @"\bPartner(?:ship|s)\b").Success) return "Partnership";
        if (Find(text, @"\bPropr[io]etor(?:ship|s)?\b|\bProprietership\b").Success) return "Proprietorship";
        if (Find(text, @"\bTrust\b").Success) return "Trust";
        if (Find(text, @"\bSociety\b").Success) return "Society";
        if (Find(text, @"\b(?:Individual|HUF|Association)\b").Success) return Find(text, @"\b(?:Individual|HUF|Association)\b").Value;
        return null;
    }

    public static PreLoginReportEntityType EntityType(BorrowerAssignmentDetails data) => data.CompanyDetails.EntityType switch
    {
        "Limited" or "Private Limited" => PreLoginReportEntityType.Company,
        "LLP" => PreLoginReportEntityType.Llp,
        "Foreign Company" => PreLoginReportEntityType.ForeignCompany,
        "Partnership" => PreLoginReportEntityType.Partnership,
        "Proprietorship" => PreLoginReportEntityType.Proprietorship,
        "Trust" => PreLoginReportEntityType.Trust,
        _ => PreLoginReportEntityType.Other
    };
}
