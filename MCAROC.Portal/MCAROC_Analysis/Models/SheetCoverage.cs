using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Excel;

namespace MCAROC_Analysis.Models;

/// <summary>Which optional workbook sheets an ingestion run's upload contained. Lets the portal and the
/// dossier say "this workbook did not include a &lt;Sheet&gt;" instead of showing the same "no records"
/// empty state whether a section was absent from the upload or was present and verified empty — a
/// distinction a BFSI reviewer needs. Built from <see cref="IngestionRun.AbsentOptionalSheetsJson"/>.</summary>
public sealed class SheetCoverage
{
    /// <summary>Every tracked optional sheet was present (or there is no completed run yet). The
    /// "not in this upload" qualifier never renders — the plain empty states stand.</summary>
    public static readonly SheetCoverage Empty = new([], false);

    private readonly HashSet<string> _absent;

    private SheetCoverage(IEnumerable<string> absent, bool chargeReportMissing)
    {
        _absent = new HashSet<string>(absent, StringComparer.Ordinal);
        ChargeReportMissing = chargeReportMissing;
    }

    public static SheetCoverage From(IngestionRun? run)
    {
        if (run is null) return Empty;

        var tracked = SheetAliases.TrackedOptionalSheets
            .Select(SheetAliases.CanonicalName).ToHashSet(StringComparer.Ordinal);

        List<string> absent;
        try
        {
            absent = JsonSerializer.Deserialize<List<string>>(run.AbsentOptionalSheetsJson) ?? [];
        }
        catch (JsonException)
        {
            absent = [];
        }

        // Only keep names we still recognise — a run recorded under an older tracked-sheet list should
        // not inflate the "M" or surface a stale sheet name.
        return new SheetCoverage(absent.Where(tracked.Contains), run.ChargeReportMissing);
    }

    /// <summary>The Detailed Charge Report workbook was not supplied (or was unusable) while the ROC
    /// report itself lists charges — the charge annexure is ROC-sequence-only.</summary>
    public bool ChargeReportMissing { get; }

    public int TotalOptionalSheets => SheetAliases.TrackedOptionalSheets.Count;
    public int PresentOptionalSheets => Math.Max(0, TotalOptionalSheets - _absent.Count);

    /// <summary>Canonical names of the tracked optional sheets absent from the upload, sorted.</summary>
    public IReadOnlyList<string> AbsentSheets => _absent.OrderBy(x => x, StringComparer.Ordinal).ToList();

    public bool AnySheetAbsent => _absent.Count > 0;

    /// <summary>True when the sheet <paramref name="aliasSet"/> names (one of the
    /// <see cref="SheetAliases"/> alias arrays) was absent from the upload.</summary>
    public bool WasAbsent(IReadOnlyList<string> aliasSet) => _absent.Contains(SheetAliases.CanonicalName(aliasSet));

    /// <summary>True only when <em>every</em> named sheet was absent — use for a tab section fed by more
    /// than one sheet (e.g. shareholding from <see cref="SheetAliases.DirectorShareholding"/> +
    /// <see cref="SheetAliases.MajorShareholding"/>), where the "not in this upload" note is right only
    /// if none of them was present.</summary>
    public bool AllAbsent(params IReadOnlyList<string>[] aliasSets) =>
        aliasSets.Length > 0 && aliasSets.All(WasAbsent);

    /// <summary>The empty-state line for a section: the "not in this upload" note when every named sheet
    /// was absent, otherwise <paramref name="presentButEmptyText"/> (the section was present and
    /// reported nothing). Pass one alias set for a single-sheet section, several for a section fed by
    /// more than one sheet.</summary>
    public string EmptyState(string presentButEmptyText, params IReadOnlyList<string>[] aliasSets) =>
        AllAbsent(aliasSets)
            ? $"This workbook did not include {Humanise([.. aliasSets.Select(SheetAliases.CanonicalName)])}."
            : presentButEmptyText;

    private static string Humanise(IReadOnlyList<string> names)
    {
        var quoted = names.Select(n => $"a “{n}” sheet").ToList();
        return quoted.Count switch
        {
            1 => quoted[0],
            2 => $"{quoted[0]} or {quoted[1]}",
            _ => string.Join(", ", quoted.Take(quoted.Count - 1)) + ", or " + quoted[^1],
        };
    }
}
