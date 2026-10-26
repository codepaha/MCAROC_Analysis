namespace MCAROC_Analysis.Services.Excel;

/// <summary>Maps a logical section to the worksheet name(s) that could contain it, since the data vendor's
/// naming can vary slightly across exports. Lookup is trim/case-insensitive/whitespace-collapsed.</summary>
public static class SheetAliases
{
    /// <summary>An LLP's export names it "About the LLP" (#539), with LLPIN, contribution and "LLP Status" rows in place of the company's.</summary>
    public static readonly string[] CompanyProfile = ["About the Company", "Company Master", "Company Details", "About the LLP"];
    public static readonly string[] Directors = ["Directors"];
    public static readonly string[] OtherDirectorships = ["Other Directorships"];
    public static readonly string[] DirectorShareholding = ["Director Shareholding"];
    public static readonly string[] MajorShareholding = ["Shareholding More Than 5%"];
    /// <summary>An LLP's export calls it "Financial Data": a statement of assets and liabilities and one of income and expenditure (#539).</summary>
    public static readonly string[] StandaloneFinancialData = ["Standalone Financial Data", "Financial Data"];
    public static readonly string[] OpenChargesSequence = ["Open Charges Sequence"];
    public static readonly string[] SatisfiedChargesSequence = ["Satisfied Charges Sequence"];
    public static readonly string[] OpenChargesDetails = ["Open Charges in Details"];
    public static readonly string[] SatisfiedChargesDetails = ["Satisfied Charges in Details"];
    public static readonly string[] Msme = ["MSME Supplier Payment Delays"];
    public static readonly string[] Gst = ["GST"];
    public static readonly string[] GstAnnexure = ["Annexure - GST"];
    public static readonly string[] Epfo = ["EPFO Establishments"];
    public static readonly string[] EpfoAnnexure = ["Annexure - EPFO Establishments"];
    public static readonly string[] Auditors = ["Auditors' Comments-Standalone", "Auditors Comments-Standalone"];
    public static readonly string[] AuditorsConsolidated = ["Auditors' Comments-Consolidated", "Auditors Comments-Consolidated"];
    public static readonly string[] LegalHistory = ["Legal History"];

    // Phase 6
    public static readonly string[] Structure = ["Structure"];
    public static readonly string[] RelatedCorporates = ["Related Corporates"];
    public static readonly string[] Compliance = ["Compliance"];
    public static readonly string[] Highlights = ["Highlights"];
    public static readonly string[] FinancialParametersAnnexure = ["Annexure - Financial Parameters"];
    public static readonly string[] SecuritiesAllotment = ["Securities Allotment"];
    public static readonly string[] Proprietorship = ["Proprietorship"];
    public static readonly string[] DirectorAssociationHistory = ["Director - Association History"];
    public static readonly string[] ConsolidatedFinancialData = ["Consolidated Financial Data"];
    public static readonly string[] LatestEventOnOpenCharges = ["Latest Event on Open Charges"];
    public static readonly string[] PeerComparison = ["Peer Comparison"];
    public static readonly string[] RelatedPartyTransactions = ["Related Party Transactions"];
    public static readonly string[] CreditRatings = ["Credit Ratings"];
    public static readonly string[] UnacceptedRatings = ["Unaccepted Ratings"];
    public static readonly string[] LegalCasesFinancialDispute = ["Legal Cases - Financial Dispute"];

    /// <summary>The optional sheets whose presence the orchestrator records per ingestion run — exactly
    /// the sheets a section parser reads in <c>IngestionOrchestrator.RunSectionParsers</c>, so every
    /// entry here is resolved through the run's <see cref="SheetPresence"/> tracker (no entry is
    /// counted in "M" without being checked). The first entry of each alias array is the canonical name
    /// written to <c>IngestionRun.AbsentOptionalSheetsJson</c> and shown to the reviewer; the count
    /// here is the "M" in "N of M optional sheets present".
    /// <para>Deliberately excluded: the charge sheets — a missing charge workbook is its own flag
    /// (<c>IngestionRun.ChargeReportMissing</c>).</para></summary>
    public static readonly IReadOnlyList<string[]> TrackedOptionalSheets =
    [
        Directors, OtherDirectorships, DirectorShareholding, MajorShareholding,
        StandaloneFinancialData, ConsolidatedFinancialData, Msme, Gst, GstAnnexure,
        Epfo, EpfoAnnexure, Auditors, AuditorsConsolidated, LegalHistory, Structure,
        RelatedCorporates, Compliance, SecuritiesAllotment, Proprietorship,
        DirectorAssociationHistory, PeerComparison, Highlights, FinancialParametersAnnexure,
        RelatedPartyTransactions, CreditRatings, UnacceptedRatings, LegalCasesFinancialDispute,
    ];

    /// <summary>The tracked sheets an LLP's export never has (#539): shareholding, allotment of securities and consolidated accounts are a
    /// company's, and so are the filings behind auditors' comments and the financial-parameters annexure (AOC-4), related-party transactions
    /// (AOC-2), MSME payment delays (MSME Form I) and related (holding / subsidiary) corporates. An LLP is neither counted short of them nor
    /// told its upload lacked them.</summary>
    public static readonly IReadOnlyList<string[]> CompanyOnlySheets =
    [
        DirectorShareholding, MajorShareholding, SecuritiesAllotment, ConsolidatedFinancialData, Auditors, AuditorsConsolidated,
        FinancialParametersAnnexure, RelatedPartyTransactions, Msme, RelatedCorporates,
    ];

    /// <summary>The canonical (reviewer-facing) name for an alias set — its first entry.</summary>
    public static string CanonicalName(IReadOnlyList<string> aliases) => aliases[0];

    public static string Normalize(string sheetName) =>
        string.Join(' ', sheetName.Trim().TrimEnd('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();

    /// <summary>Finds the first sheet in the workbook matching any of the given aliases, or null if absent.</summary>
    public static SheetData? Find(IReadOnlyList<SheetData> workbook, IReadOnlyList<string> aliases)
    {
        var normalizedAliases = aliases.Select(Normalize).ToHashSet();
        return workbook.FirstOrDefault(s => normalizedAliases.Contains(Normalize(s.Name)));
    }
}
