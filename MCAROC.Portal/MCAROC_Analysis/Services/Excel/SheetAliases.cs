namespace MCAROC_Analysis.Services.Excel;

/// <summary>Maps a logical section to the worksheet name(s) that could contain it, since the data vendor's
/// naming can vary slightly across exports. Lookup is trim/case-insensitive/whitespace-collapsed.</summary>
public static class SheetAliases
{
    public static readonly string[] CompanyProfile = ["About the Company", "Company Master", "Company Details"];
    public static readonly string[] Directors = ["Directors"];
    public static readonly string[] OtherDirectorships = ["Other Directorships"];
    public static readonly string[] DirectorShareholding = ["Director Shareholding"];
    public static readonly string[] MajorShareholding = ["Shareholding More Than 5%"];
    public static readonly string[] StandaloneFinancialData = ["Standalone Financial Data"];
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

    /// <summary>The optional sheets whose presence the orchestrator records per ingestion run. The first
    /// entry of each alias array is the canonical name written to <c>IngestionRun.AbsentOptionalSheetsJson</c>
    /// and shown to the reviewer; the count here is the "M" in "N of M optional sheets present". Charge
    /// sheets are excluded — a missing charge workbook is its own flag (<c>IngestionRun.ChargeReportMissing</c>).</summary>
    public static readonly IReadOnlyList<string[]> TrackedOptionalSheets =
    [
        Directors, OtherDirectorships, DirectorShareholding, MajorShareholding,
        StandaloneFinancialData, ConsolidatedFinancialData, Msme, Gst, GstAnnexure,
        EpfoAnnexure, Auditors, AuditorsConsolidated, LegalHistory, Structure,
        RelatedCorporates, Compliance, SecuritiesAllotment, Proprietorship,
        DirectorAssociationHistory, PeerComparison, Highlights, FinancialParametersAnnexure,
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
