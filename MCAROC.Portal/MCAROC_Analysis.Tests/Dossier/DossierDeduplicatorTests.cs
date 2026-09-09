using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Dossier;

namespace MCAROC_Analysis.Tests.Dossier;

public class DossierDeduplicatorTests
{
    [Fact]
    public void MergeShareholders_collapses_same_holder_year_and_keeps_the_richer_row()
    {
        var rows = new[]
        {
            new Shareholding { ShareholderNameNormalized = "RAMESH RAO", FinancialYear = 2024, HoldingPercentage = 10.8m },
            new Shareholding { ShareholderNameNormalized = "RAMESH RAO", FinancialYear = 2024, HoldingPercentage = 10.8m, SharesHeld = 1_000_000, ShareholderType = "Individual" },
            new Shareholding { ShareholderNameNormalized = "FAMILY TRUST", FinancialYear = 2023, HoldingPercentage = 3.4m },
        };

        var merged = DossierDeduplicator.MergeShareholders(rows);

        Assert.Equal(2, merged.Count);
        var rao = Assert.Single(merged, s => s.ShareholderNameNormalized == "RAMESH RAO");
        Assert.Equal(1_000_000, rao.SharesHeld);     // the fuller of the two rows won
        Assert.Equal("Individual", rao.ShareholderType);
    }

    [Fact]
    public void ThreadLitigation_groups_interlocutory_apps_under_the_parent_and_flags_the_grouping()
    {
        var cases = new[]
        {
            new Litigation { CaseNumber = "CP(IB) No. 214/BB/2019", CaseStatus = "Pending" },
            new Litigation { CaseNumber = "IA(IB) No. 88/2026 in CP(IB) No. 214/BB/2019", CaseStatus = "Pending" },
            new Litigation { CaseNumber = "IA(IB) No. 61/2025 in CP(IB) No. 214/BB/2019", CaseStatus = "Disposed" },
            new Litigation { CaseNumber = "OA No. 445/2022", CaseStatus = "Pending" }, // unrelated — its own thread
        };

        var threads = DossierDeduplicator.ThreadLitigation(cases);

        Assert.Equal(2, threads.Count);
        var nclt = threads.Single(t => t.Cases.Count == 3);
        Assert.True(nclt.GroupedAutomatically);
        Assert.Equal(2, nclt.PendingCount);

        var drt = threads.Single(t => t.Cases.Count == 1);
        Assert.False(drt.GroupedAutomatically);
        Assert.Equal("OA No. 445/2022", drt.Primary.CaseNumber);
    }

    [Fact]
    public void ThreadLitigation_never_merges_two_different_parent_case_numbers()
    {
        var cases = new[]
        {
            new Litigation { CaseNumber = "CP(IB) No. 214/BB/2019", CaseStatus = "Pending" },
            new Litigation { CaseNumber = "CP(IB) No. 593/KB/2017", CaseStatus = "Pending" },
        };

        var threads = DossierDeduplicator.ThreadLitigation(cases);

        Assert.Equal(2, threads.Count);
        Assert.All(threads, t => Assert.False(t.GroupedAutomatically));
    }

    [Fact]
    public void SummariseSuitFiled_collapses_quarters_but_reports_how_many_and_the_latest()
    {
        var records = new[]
        {
            new ComplianceRecord { RecordType = ComplianceRecordType.SuitFiled, Bank = "IDBI BANK", AmountCrore = 12m, DefaulterType = "Defaulter - Suit Filed", RecordDate = new DateOnly(2014, 3, 31) },
            new ComplianceRecord { RecordType = ComplianceRecordType.SuitFiled, Bank = "IDBI BANK", AmountCrore = 12m, DefaulterType = "Defaulter - Suit Filed", RecordDate = new DateOnly(2014, 6, 30) },
            new ComplianceRecord { RecordType = ComplianceRecordType.SuitFiled, Bank = "IDBI BANK", AmountCrore = 12m, DefaulterType = "Defaulter - Suit Filed", RecordDate = new DateOnly(2014, 9, 30) },
            new ComplianceRecord { RecordType = ComplianceRecordType.Cdr, RecordDate = new DateOnly(2014, 4, 28) },
        };

        var summary = DossierDeduplicator.SummariseSuitFiled(records);

        var row = Assert.Single(summary);
        Assert.Equal("IDBI BANK", row.Bank);
        Assert.Equal(3, row.Quarters);
        Assert.Equal(new DateOnly(2014, 9, 30), row.Latest);
    }
}
