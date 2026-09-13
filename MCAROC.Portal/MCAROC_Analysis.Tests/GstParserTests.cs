using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;
using static MCAROC_Analysis.Tests.TestHelpers;

namespace MCAROC_Analysis.Tests;

public class GstParserTests
{
    [Fact]
    public void CapturesJurisdictionAndLegalNameColumns()
    {
        var sheet = Sheet("GST",
            Row("GSTIN", "GSTIN STATUS", "STATE", "RETURN TYPE", "LATEST FILING(S)", "FINANCIAL YEAR", "TAX PERIOD",
                "DATE OF REGISTRATION", "CENTRE JURISDICTION", "STATE JURISDICTION", "TAXPAYER TYPE",
                "LEGAL NAME OF BUSINESS", "TRADE NAME", "NATURE OF BUSINESS ACTIVITIES", "FLAGS", "FILING DETAILS"),
            Row("14AABCC1907E1ZC", "Active", "Manipur", "GSTR1", "10 Aug, 2026", "2026-2027", "July",
                "1 Jul, 2017", "IMPHAL II RANGE", "Work Contracts", "Regular",
                "COASTAL PROJECTS LIMITED", "M/S COASTAL PROJECT LIMITED", "Works Contract",
                "Filed After Due Date in last 12 Months", "See Annexure - GST for Filing Details"));

        var result = GstParser.Parse(sheet, null, 1, 1, 10);

        var reg = Assert.Single(result.Registrations.Items);
        Assert.Equal("IMPHAL II RANGE", reg.CentreJurisdiction);
        Assert.Equal("Work Contracts", reg.StateJurisdiction);
        Assert.Equal("COASTAL PROJECTS LIMITED", reg.LegalNameOfBusiness);
        Assert.Equal("M/S COASTAL PROJECT LIMITED", reg.TradeName);
    }

    /// <summary>Regression test for GST_ADDITIONAL_REGISTRATION_ROW — the real GST sheet carries one row
    /// per (GSTIN, return type), so the same GSTIN legitimately repeats. This was reported as "unit
    /// tested" in the #150 ingestion-warnings review but had no actual coverage; this closes that gap.</summary>
    [Fact]
    public void SecondRowForTheSameGstinIsFlaggedNotDuplicatedAndDoesNotCorruptTheFirstRegistration()
    {
        var sheet = Sheet("GST",
            Row("GSTIN", "GSTIN STATUS", "STATE", "RETURN TYPE", "LATEST FILING(S)", "FINANCIAL YEAR", "TAX PERIOD",
                "DATE OF REGISTRATION", "CENTRE JURISDICTION", "STATE JURISDICTION", "TAXPAYER TYPE",
                "LEGAL NAME OF BUSINESS", "TRADE NAME", "NATURE OF BUSINESS ACTIVITIES", "FLAGS", "FILING DETAILS"),
            Row("29AALCA3226E1ZT", "Active", "Karnataka", "GSTR3B", "20 Aug, 2026", "2026-2027", "August",
                "1 Jul, 2017", "BANGALORE RANGE", "Work Contracts", "Regular",
                "TEST COMPANY LIMITED", "TEST CO", "Works Contract", "-", "See Annexure - GST for Filing Details"),
            Row("29AALCA3226E1ZT", "Active", "Karnataka", "GSTR1", "10 Aug, 2026", "2026-2027", "July",
                "1 Jul, 2017", "SHOULD NOT OVERWRITE", "SHOULD NOT OVERWRITE", "Regular",
                "SHOULD NOT OVERWRITE", "SHOULD NOT OVERWRITE", "Works Contract", "-", "See Annexure - GST for Filing Details"));

        var result = GstParser.Parse(sheet, null, 1, 1, 10);

        // Exactly one registration for the GSTIN, keeping the FIRST row's data untouched by the second.
        var reg = Assert.Single(result.Registrations.Items);
        Assert.Equal("29AALCA3226E1ZT", reg.Gstin);
        Assert.Equal("BANGALORE RANGE", reg.CentreJurisdiction);
        Assert.Equal("TEST COMPANY LIMITED", reg.LegalNameOfBusiness);
        Assert.Equal("TEST CO", reg.TradeName);

        // The second row is not silently dropped - it's flagged with the specific issue code.
        var warning = Assert.Single(result.Registrations.Warnings);
        Assert.Equal("GST_ADDITIONAL_REGISTRATION_ROW", warning.IssueCode);
        Assert.Equal("Gstin", warning.FieldName);
        Assert.Equal("29AALCA3226E1ZT", warning.RawValue);
        Assert.Contains("GSTR1", warning.Message);
        Assert.Contains("July", warning.Message);
    }
}
