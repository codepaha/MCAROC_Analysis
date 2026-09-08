using MCAROC_Analysis.Services.Excel;

namespace MCAROC_Analysis.Tests;

public static class TestHelpers
{
    public static IReadOnlyList<object?> Row(params object?[] cells) => cells;

    public static SheetData Sheet(string name, params IReadOnlyList<object?>[] rows) => new(name, rows);
}
