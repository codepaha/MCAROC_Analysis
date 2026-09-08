using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;
using static MCAROC_Analysis.Tests.TestHelpers;

namespace MCAROC_Analysis.Tests;

public class ChargesParserTests
{
    private static SheetData OpenSequenceSheet() => Sheet("Open Charges Sequence",
        Row("SERIAL NUMBER", "CHARGE ID", "STATUS", "DATE", "FILING DATE", "HOLDER NAME", "CHARGE AMOUNT (Rs. Crore)", "PROPERTY TYPE", "NUMBER OF HOLDERS"),
        Row(1.1, 101265730.0, "Creation", "10 Mar, 2026", "6 Apr, 2026", "Jakson Green Vehicles Private Limited", 160.0, "Intangible - Patent", 1.0));

    private static IReadOnlyList<SheetData> RocWorkbook(SheetData openSeq) => [openSeq];

    [Fact]
    public void EventsAlwaysComeFromRocSequenceSheetEvenWithoutChargeReport()
    {
        var result = ChargesParser.Parse(RocWorkbook(OpenSequenceSheet()), chargeWorkbook: null,
            chargeWorkbookIdentityMatches: false, requestId: 1, ingestionRunId: 1, rocSourceDocumentId: 10, chargeSourceDocumentId: null);

        var charge = Assert.Single(result.Items);
        Assert.Equal("101265730", charge.RocChargeNumber);
        Assert.Equal("Open", charge.ChargeStatus);
        var ev = Assert.Single(charge.Events);
        Assert.Equal(ChargeEventMatchConfidence.Unmatched, ev.MatchConfidence);
        Assert.Null(ev.InstrumentDescription);
    }

    [Fact]
    public void ExactMatchEnrichesFromChargeReportDetailSheet()
    {
        var detailSheet = Sheet("Open Charges in Details",
            Row("SERIAL NUMBER", "CHARGE ID", "STATUS", "DATE", "FILING DATE", "HOLDER NAME", "AMOUNT (Rs. Crore)", "PROPERTY TYPE", "NUMBER OF HOLDERS",
                "INSTRUMENT DESCRIPTION", "RATE OF INTEREST", "TERMS OF PAYMENT", "PROPERTY PARTICULARS", "EXTENT AND OPERATION", "OTHER TERMS",
                "MODIFICATION PARTICULARS", "JOINT HOLDING", "CONSORTIUM HOLDING"),
            Row(1.1, 101265730.0, "Creation", "10 Mar, 2026", "6 Apr, 2026", "Jakson Green Vehicles Private Limited", 160.0, "Intangible - Patent", 1.0,
                "Loan secured by patent pledge", "10.5%", "As per agreement", "Company patent IP", "As per agreement", "-", "-", "NO", "NO"));

        var result = ChargesParser.Parse(RocWorkbook(OpenSequenceSheet()), chargeWorkbook: [detailSheet],
            chargeWorkbookIdentityMatches: true, requestId: 1, ingestionRunId: 1, rocSourceDocumentId: 10, chargeSourceDocumentId: 11);

        var ev = Assert.Single(Assert.Single(result.Items).Events);
        Assert.Equal(ChargeEventMatchConfidence.Exact, ev.MatchConfidence);
        Assert.Equal("Loan secured by patent pledge", ev.InstrumentDescription);
        Assert.Equal(false, ev.JointHolding);
    }

    [Fact]
    public void QuarantinedChargeReportIsNeverUsedForEnrichment()
    {
        var detailSheet = Sheet("Open Charges in Details",
            Row("SERIAL NUMBER", "CHARGE ID", "STATUS", "DATE", "FILING DATE", "HOLDER NAME", "AMOUNT (Rs. Crore)", "PROPERTY TYPE", "NUMBER OF HOLDERS",
                "INSTRUMENT DESCRIPTION", "RATE OF INTEREST", "TERMS OF PAYMENT", "PROPERTY PARTICULARS", "EXTENT AND OPERATION", "OTHER TERMS",
                "MODIFICATION PARTICULARS", "JOINT HOLDING", "CONSORTIUM HOLDING"),
            Row(1.1, 101265730.0, "Creation", "10 Mar, 2026", "6 Apr, 2026", "Jakson Green Vehicles Private Limited", 160.0, "Intangible - Patent", 1.0,
                "Should never be attached", "10.5%", "-", "-", "-", "-", "-", "NO", "NO"));

        // chargeWorkbookIdentityMatches: false simulates the orchestrator having detected a CIN mismatch
        // between the ROC report and the charge report — enrichment must be skipped entirely.
        var result = ChargesParser.Parse(RocWorkbook(OpenSequenceSheet()), chargeWorkbook: [detailSheet],
            chargeWorkbookIdentityMatches: false, requestId: 1, ingestionRunId: 1, rocSourceDocumentId: 10, chargeSourceDocumentId: 11);

        var ev = Assert.Single(Assert.Single(result.Items).Events);
        Assert.Equal(ChargeEventMatchConfidence.Unmatched, ev.MatchConfidence);
        Assert.Null(ev.InstrumentDescription);
    }

    [Fact]
    public void ReconstructsFullLifecycleAcrossMultipleEvents()
    {
        var sequenceSheet = Sheet("Open Charges Sequence",
            Row("SERIAL NUMBER", "CHARGE ID", "STATUS", "DATE", "FILING DATE", "HOLDER NAME", "CHARGE AMOUNT (Rs. Crore)", "PROPERTY TYPE", "NUMBER OF HOLDERS"),
            Row(1.1, 100894800.0, "Creation", "8 Mar, 2024", "8 Apr, 2024", "CKERS FINANCE PRIVATE LIMITED", 5.0, "Movable property", 3.0),
            Row(1.2, 100894800.0, "Modification", "13 Sep, 2024", "18 Sep, 2024", "CKERS FINANCE PRIVATE LIMITED", 5.0, "Movable property", 2.0));

        var satisfiedSheet = Sheet("Satisfied Charges Sequence",
            Row("SERIAL NUMBER", "CHARGE ID", "STATUS", "DATE", "FILING DATE", "HOLDER NAME", "CHARGE AMOUNT (Rs. Crore)", "PROPERTY TYPE", "NUMBER OF HOLDERS"),
            Row(1.3, 100894800.0, "Satisfaction", "15 Apr, 2026", "16 Apr, 2026", "CKERS FINANCE PRIVATE LIMITED", 5.0, "-", "-"));

        var result = ChargesParser.Parse([sequenceSheet, satisfiedSheet], null, false, 1, 1, 10, null);

        var charge = Assert.Single(result.Items);
        Assert.Equal(3, charge.Events.Count);
        Assert.Equal("Satisfied", charge.ChargeStatus);
        Assert.Equal(new DateOnly(2024, 3, 8), charge.CreationDate);
        Assert.Equal(new DateOnly(2026, 4, 15), charge.SatisfactionDate);
    }
}
