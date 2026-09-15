using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;
using static MCAROC_Analysis.Tests.TestHelpers;

namespace MCAROC_Analysis.Tests;

/// <summary>Regression coverage for a real bug found against live data: the sheet stacks a 5-column
/// year-summary table and a 7-column per-note detail table (whose comment text lives in column 4, not
/// column 2 — column 2 is a numeric "Section" code that looks like a plausible year/id if unchecked).</summary>
public class AuditorsParserTests
{
    [Fact]
    public void ReadsYearSummaryTableCorrectly()
    {
        var sheet = Sheet("Auditors' Comments-Standalone",
            Row("AUDITORS' COMMENTS - STANDALONE", "", "", "", "", "", ""),
            Row("Financial Year", "Qualified?", "", "", "Comments Given By", "", ""),
            Row(2025.0, "Yes", "", "", "- SAXENA RAJEEV KUMAR", "", ""),
            Row(2024.0, "No", "", "", "- Pankaj Agarwal", "", ""));

        var result = AuditorsParser.Parse(sheet, 1, 1, 10);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2025, result.Items[0].FinancialYear);
        Assert.True(result.Items[0].HasQualificationOrAdverseRemark);
        Assert.Equal("- SAXENA RAJEEV KUMAR", result.Items[0].ObservationText);
        // Name-only text doesn't match the "NAME (Membership Number: X) of FIRM (Registration Number: Y)"
        // format — AuditorName keeps the raw text and the structured fields stay null.
        Assert.Equal("- SAXENA RAJEEV KUMAR", result.Items[0].AuditorName);
        Assert.Null(result.Items[0].MembershipNumber);
    }

    [Fact]
    public void SplitsAuditorNameMembershipAndFirmFromCommentsGivenBy()
    {
        var sheet = Sheet("Auditors' Comments-Standalone",
            Row("AUDITORS' COMMENTS - STANDALONE"),
            Row("Financial Year", "Qualified?", "", "", "Comments Given By"),
            Row(2016.0, "No", "", "",
                "- MANAS KUMAR MANIA (Membership Number: 300113) of U K MAHAPATRA & CO (Registration Number: 320039E)."));

        var result = AuditorsParser.Parse(sheet, 1, 1, 10);

        var obs = Assert.Single(result.Items);
        Assert.Equal("MANAS KUMAR MANIA", obs.AuditorName);
        Assert.Equal("300113", obs.MembershipNumber);
        Assert.Equal("U K MAHAPATRA & CO", obs.FirmName);
        Assert.Equal("320039E", obs.FirmRegistrationNumber);
    }

    [Fact]
    public void DoesNotConfuseSectionCodeColumnWithCommentText()
    {
        var sheet = Sheet("Auditors' Comments-Standalone",
            Row("AUDITORS' COMMENTS - STANDALONE"),
            Row("Financial Year", "Qualified?", "", "", "Comments Given By"),
            Row(2025.0, "Yes", "", "", "- SAXENA RAJEEV KUMAR"),
            Row("", "", "", "", ""),
            Row("Serial Number", "Financial Year", "Section", "Section Name", "Auditors' Comments", "Directors' Comments", "Footnotes"),
            Row(1.0, 2025.0, 700600.0, "Disclosures - Directors' Report", "The real observation text", "Self explanatory", "-"),
            Row(2.0, 2024.0, 700400.0, "Disclosures - Auditors' Report", "NIL", "-", "-"));

        var result = AuditorsParser.Parse(sheet, 1, 1, 10);

        // 1 from the summary table + 1 from the detail table (the "NIL" row is skipped as non-informative)
        Assert.Equal(2, result.Items.Count);
        var detailRow = result.Items[1];
        Assert.Equal(2025, detailRow.FinancialYear); // not 700600, and not the serial number 1
        Assert.Equal("The real observation text", detailRow.ObservationText);

        // G18: the remaining detail-table columns now land on the same row.
        Assert.Equal(1, detailRow.SerialNumber);
        Assert.Equal("700600", detailRow.SectionCode);
        Assert.Equal("Disclosures - Directors' Report", detailRow.SectionName);
        Assert.Equal("Self explanatory", detailRow.DirectorsComments);
        Assert.Null(detailRow.Footnotes); // "-" normalizes to null
    }

    [Fact]
    public void RowWithBlankAuditorsCommentsButRealDirectorsCommentsIsStillKept()
    {
        var sheet = Sheet("Auditors' Comments-Standalone",
            Row("AUDITORS' COMMENTS - STANDALONE"),
            Row("Financial Year", "Qualified?", "", "", "Comments Given By"),
            Row("", "", "", "", ""),
            Row("Serial Number", "Financial Year", "Section", "Section Name", "Auditors' Comments", "Directors' Comments", "Footnotes"),
            Row(1.0, 2025.0, 700600.0, "Disclosures", "NIL", "Board noted the delay", "-"));

        var result = AuditorsParser.Parse(sheet, 1, 1, 10);

        // Auditors' Comments is NIL, but Directors' Comments has real content — must not be dropped.
        var row = Assert.Single(result.Items);
        Assert.Null(row.ObservationText);
        Assert.Equal("Board noted the delay", row.DirectorsComments);
        Assert.Equal("700600", row.SectionCode);
    }

    [Fact]
    public void RowWithBlankAuditorsCommentsButRealFootnoteIsStillKept()
    {
        var sheet = Sheet("Auditors' Comments-Standalone",
            Row("AUDITORS' COMMENTS - STANDALONE"),
            Row("Financial Year", "Qualified?", "", "", "Comments Given By"),
            Row("", "", "", "", ""),
            Row("Serial Number", "Financial Year", "Section", "Section Name", "Auditors' Comments", "Directors' Comments", "Footnotes"),
            Row(1.0, 2025.0, 700600.0, "Disclosures", "-", "-", "Refer note 12"));

        var result = AuditorsParser.Parse(sheet, 1, 1, 10);

        var row = Assert.Single(result.Items);
        Assert.Null(row.ObservationText);
        Assert.Null(row.DirectorsComments);
        Assert.Equal("Refer note 12", row.Footnotes);
    }

    [Fact]
    public void RowWithAllThreeTextFieldsBlankIsStillSkipped()
    {
        var sheet = Sheet("Auditors' Comments-Standalone",
            Row("AUDITORS' COMMENTS - STANDALONE"),
            Row("Financial Year", "Qualified?", "", "", "Comments Given By"),
            Row("", "", "", "", ""),
            Row("Serial Number", "Financial Year", "Section", "Section Name", "Auditors' Comments", "Directors' Comments", "Footnotes"),
            Row(1.0, 2025.0, 700600.0, "Disclosures", "NIL", "-", ""));

        var result = AuditorsParser.Parse(sheet, 1, 1, 10);

        Assert.Empty(result.Items);
    }

    [Fact]
    public void SectionNameAndFootnotesNormalizeDashAndNilToNullJustLikeDirectorsComments()
    {
        var sheet = Sheet("Auditors' Comments-Standalone",
            Row("AUDITORS' COMMENTS - STANDALONE"),
            Row("Financial Year", "Qualified?", "", "", "Comments Given By"),
            Row("", "", "", "", ""),
            Row("Serial Number", "Financial Year", "Section", "Section Name", "Auditors' Comments", "Directors' Comments", "Footnotes"),
            Row(1.0, 2025.0, 700600.0, "-", "A real comment", "-", "NIL"));

        var result = AuditorsParser.Parse(sheet, 1, 1, 10);

        var row = Assert.Single(result.Items);
        Assert.Null(row.SectionName); // "-" normalizes, same rule as DirectorsComments
        Assert.Null(row.Footnotes);   // "NIL" normalizes, same rule as DirectorsComments
        Assert.Equal("A real comment", row.ObservationText);
    }

    [Fact]
    public void NonIntegralSerialNumberIsRejectedNotTruncated()
    {
        var sheet = Sheet("Auditors' Comments-Standalone",
            Row("AUDITORS' COMMENTS - STANDALONE"),
            Row("Financial Year", "Qualified?", "", "", "Comments Given By"),
            Row("", "", "", "", ""),
            Row("Serial Number", "Financial Year", "Section", "Section Name", "Auditors' Comments", "Directors' Comments", "Footnotes"),
            Row(1.5, 2025.0, 700600.0, "Disclosures", "A real comment", "-", "-"));

        var result = AuditorsParser.Parse(sheet, 1, 1, 10);

        var row = Assert.Single(result.Items);
        Assert.Null(row.SerialNumber); // left unset, never silently truncated to 1
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("AUDITOR_SERIAL_NUMBER_NOT_INTEGRAL", warning.IssueCode);
        Assert.Contains("1.5", warning.Message);
    }

    [Fact]
    public void WholeNumberSerialNumberParsesCleanlyWithNoWarning()
    {
        var sheet = Sheet("Auditors' Comments-Standalone",
            Row("AUDITORS' COMMENTS - STANDALONE"),
            Row("Financial Year", "Qualified?", "", "", "Comments Given By"),
            Row("", "", "", "", ""),
            Row("Serial Number", "Financial Year", "Section", "Section Name", "Auditors' Comments", "Directors' Comments", "Footnotes"),
            Row(2.0, 2025.0, 700600.0, "Disclosures", "A real comment", "-", "-"));

        var result = AuditorsParser.Parse(sheet, 1, 1, 10);

        var row = Assert.Single(result.Items);
        Assert.Equal(2, row.SerialNumber);
        Assert.Empty(result.Warnings);
    }

    /// <summary>Regression for a real bug found in review: an integral Serial Number value above
    /// Int32.MaxValue was cast directly to int, which throws OverflowException (decimal-to-int
    /// conversions are checked unconditionally in .NET) and would abort ingestion for one bad cell
    /// instead of leaving the field unset with a warning.</summary>
    [Fact]
    public void IntegralSerialNumberAboveInt32MaxValueIsRejectedNotOverflowed()
    {
        var sheet = Sheet("Auditors' Comments-Standalone",
            Row("AUDITORS' COMMENTS - STANDALONE"),
            Row("Financial Year", "Qualified?", "", "", "Comments Given By"),
            Row("", "", "", "", ""),
            Row("Serial Number", "Financial Year", "Section", "Section Name", "Auditors' Comments", "Directors' Comments", "Footnotes"),
            Row(2147483648.0, 2025.0, 700600.0, "Disclosures", "A real comment", "-", "-"));

        // Must not throw — this is the actual bug: a bare (int) cast on this value throws OverflowException.
        var result = AuditorsParser.Parse(sheet, 1, 1, 10);

        var row = Assert.Single(result.Items);
        Assert.Null(row.SerialNumber); // left unset, never overflowed
        Assert.Equal("A real comment", row.ObservationText); // the rest of the row still parses fine
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("AUDITOR_SERIAL_NUMBER_OUT_OF_RANGE", warning.IssueCode);
        Assert.Contains("2147483648", warning.Message);
    }

    /// <summary>Regression found via real-file E2E testing: a literal "-" in "Comments Given By" (no
    /// comment on file for that year) propagated as a real value into both AuditorName and
    /// ObservationText, unlike the sibling detail table's text columns which already normalize "-"/"NIL"
    /// to null. Once the dossier PDF started prefixing the Comment cell with auditor-identity text (for
    /// identity recovered from elsewhere), this left a stray "— -" tail in the final report.</summary>
    [Fact]
    public void DashCommentsGivenBy_NormalizesToNull_NotLiteralDash()
    {
        var sheet = Sheet("Auditors' Comments-Standalone",
            Row("AUDITORS' COMMENTS - STANDALONE"),
            Row("Financial Year", "Qualified?", "", "", "Comments Given By"),
            Row(2025.0, "No", "", "", "-"));

        var result = AuditorsParser.Parse(sheet, 1, 1, 10);

        var obs = Assert.Single(result.Items);
        Assert.Null(obs.ObservationText);
        Assert.Null(obs.AuditorName);
    }
}
