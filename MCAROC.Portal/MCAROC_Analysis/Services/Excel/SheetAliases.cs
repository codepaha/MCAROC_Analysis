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
    public static readonly string[] LegalHistory = ["Legal History"];

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
