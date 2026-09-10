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
        Assert.Equal("Disposed", probable.CaseStatus);
        Assert.Equal("COMMERCIAL COURT", probable.Court);
        Assert.Equal("Com.O.S. /280/2026", probable.CaseNumber);
        Assert.Equal("MAGCORE LAMINATION vs. ALTIGREEN PROPULSION LABS", probable.Litigants);
    }

    [Fact]
    public void UnverifiedCourtRecordsUseTheirOwnShorterLayoutAndDoNotBleedIntoProbable()
    {
        // The real regression: the "UNVERIFIED COURT RECORDS" section has a different, shorter layout
        // (Court | Petitioner | Respondent | Case No. | Date) and used to be parsed with the PROBABLE
        // column map — dropping the court name into Case Status and losing the case number.
        var sheet = Sheet("Legal History",
            Row("LEGAL HISTORY"),
            Row("Case Type", "Case Status", "Case Category", "Court", "Litigant(s)", "Case No.", "Date of Last Hearing / Judgement"),
            Row("Filed Against this Corporate", "Disposed", "Insolvency", "NCLT", "TCI LIMITED", "36(BEN)2025", "16 Dec, 2025"),
            Row("", "", "", "", "", "", ""),
            Row("PROBABLE CASES"),
            Row("Case Status", "Case Category", "Court", "Petitioner(s)", "Respondent(s)", "Case No.", "Date of Last Hearing / Judgement"),
            Row("Pending", "Insolvency", "NCLAT", "HPPCL", "M/s Coastal Project Pvt Ltd", "Company Appeal(AT)(Ins) - 935/2023", "5 Mar, 2025"),
            Row("UNVERIFIED COURT RECORDS"),
            Row("Court", "Petitioner(s)", "Respondent(s)", "Case No.", "Date of Last Activity"),
            Row("CCH1 PRL. CITY CIVIL AND SESSIONS JUDGE", "BHARATH HEAVY ELECTRICALS LIMITED", "M/S COASTAL PROJECTS LIMITED", "AA/319/2018", "-"));

        var result = LegalHistoryParser.Parse(sheet, 1, 1, 10);

        Assert.Equal(3, result.Items.Count);

        var probable = result.Items[1];
        Assert.Equal(LitigationMatchStatus.Probable, probable.MatchStatus);
        Assert.Equal("Pending", probable.CaseStatus); // not the court name of the row below

        var unverified = result.Items[2];
        Assert.Equal(LitigationMatchStatus.Uncertain, unverified.MatchStatus);
        Assert.Null(unverified.CaseType);
        Assert.Null(unverified.CaseStatus);
        Assert.Null(unverified.CaseCategory);
        Assert.Equal("CCH1 PRL. CITY CIVIL AND SESSIONS JUDGE", unverified.Court);
        Assert.Equal("BHARATH HEAVY ELECTRICALS LIMITED vs. M/S COASTAL PROJECTS LIMITED", unverified.Litigants);
        Assert.Equal("AA/319/2018", unverified.CaseNumber);
    }
}
