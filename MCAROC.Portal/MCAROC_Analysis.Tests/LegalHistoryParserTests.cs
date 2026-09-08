using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;
using static MCAROC_Analysis.Tests.TestHelpers;

namespace MCAROC_Analysis.Tests;

/// <summary>Regression coverage for a real finding against live data: the sheet stacks confirmed litigation
/// and a "PROBABLE CASES" sub-table with a different column layout (Petitioner/Respondent instead of a
/// single Litigant(s) column, and no Case Type column) — exactly what Litigation.MatchStatus exists for.</summary>
public class LegalHistoryParserTests
{
    [Fact]
    public void ConfirmedCasesGetConfirmedMatchStatus()
    {
        var sheet = Sheet("Legal History",
            Row("LEGAL HISTORY"),
            Row("Case Type", "Case Status", "Case Category", "Court", "Litigant(s)", "Case No.", "Date of Last Hearing / Judgement"),
            Row("Filed Against this Corporate", "Disposed", "Insolvency", "NCLT", "TCI LIMITED", "36(BEN)2025", "16 Dec, 2025"));

        var result = LegalHistoryParser.Parse(sheet, 1, 1, 10);

        var litigation = Assert.Single(result.Items);
        Assert.Equal(LitigationMatchStatus.Confirmed, litigation.MatchStatus);
        Assert.Equal("Filed Against this Corporate", litigation.CaseType);
    }

    [Fact]
    public void ProbableCasesGetProbableMatchStatusAndCombinedLitigants()
    {
        var sheet = Sheet("Legal History",
            Row("LEGAL HISTORY"),
            Row("Case Type", "Case Status", "Case Category", "Court", "Litigant(s)", "Case No.", "Date of Last Hearing / Judgement"),
            Row("Filed Against this Corporate", "Disposed", "Insolvency", "NCLT", "TCI LIMITED", "36(BEN)2025", "16 Dec, 2025"),
            Row("", "", "", "", "", "", ""),
            Row("PROBABLE CASES"),
            Row("Case Status", "Case Category", "Court", "Petitioner(s)", "Respondent(s)", "Case No.", "Date of Last Hearing / Judgement"),
            Row("Disposed", "Others", "COMMERCIAL COURT", "MAGCORE LAMINATION", "ALTIGREEN PROPULSION LABS", "Com.O.S. /280/2026", "2 Jun, 2026"));

        var result = LegalHistoryParser.Parse(sheet, 1, 1, 10);

        Assert.Equal(2, result.Items.Count);
        var probable = result.Items[1];
        Assert.Equal(LitigationMatchStatus.Probable, probable.MatchStatus);
        Assert.Null(probable.CaseType); // this sub-table has no Case Type column
        Assert.Equal("MAGCORE LAMINATION vs. ALTIGREEN PROPULSION LABS", probable.Litigants);
    }
}
