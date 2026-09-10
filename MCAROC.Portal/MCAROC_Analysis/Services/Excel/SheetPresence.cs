namespace MCAROC_Analysis.Services.Excel;

/// <summary>Wraps one workbook for the ingestion run and records which optional sheets it did not
/// contain. <see cref="Find"/> resolves a sheet exactly like <see cref="SheetAliases.Find"/> but,
/// when the sheet is absent and the alias set is one of <see cref="SheetAliases.TrackedOptionalSheets"/>,
/// adds its canonical name to <see cref="AbsentCanonicalNames"/>. The orchestrator persists that list
/// to <c>IngestionRun.AbsentOptionalSheetsJson</c> so the portal and dossier can distinguish
/// "sheet not in this upload" from "sheet present, reported no records".</summary>
public sealed class SheetPresence(IReadOnlyList<SheetData> workbook)
{
    private static readonly HashSet<string> TrackedCanonical =
        SheetAliases.TrackedOptionalSheets.Select(SheetAliases.CanonicalName).ToHashSet(StringComparer.Ordinal);

    private readonly SortedSet<string> _absent = new(StringComparer.Ordinal);

    /// <summary>The tracked-optional sheet that resolves to this alias set, or null if absent. An absent
    /// tracked sheet is recorded; pass a non-tracked alias set (e.g. the required company sheet) and it
    /// is simply resolved without being recorded.</summary>
    public SheetData? Find(IReadOnlyList<string> aliases)
    {
        var sheet = SheetAliases.Find(workbook, aliases);
        if (sheet is null)
        {
            var canonical = SheetAliases.CanonicalName(aliases);
            if (TrackedCanonical.Contains(canonical)) _absent.Add(canonical);
        }
        return sheet;
    }

    /// <summary>Canonical names of the tracked optional sheets absent from this workbook, sorted.</summary>
    public IReadOnlyList<string> AbsentCanonicalNames => _absent.ToList();
}
