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
}
