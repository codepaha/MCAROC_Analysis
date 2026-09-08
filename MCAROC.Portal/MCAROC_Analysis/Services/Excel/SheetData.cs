namespace MCAROC_Analysis.Services.Excel;

/// <summary>A worksheet's raw grid, exactly as it appears in the source file (row 0 = spreadsheet row 1).
/// No header inference — sheets in these reports have varying title/header row positions, so each
/// parser decides for itself where its data starts.</summary>
public record SheetData(string Name, IReadOnlyList<IReadOnlyList<object?>> Rows);
