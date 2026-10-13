using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>The module boundary (plan section 12.3): nothing outside <c>Services/LitigationModule</c> may reference the module's entities or its store namespace.
/// Consumers use the module's reader and writer. The model hook in <c>AppDbContext</c> and the generated migrations are the only exceptions.</summary>
public class LitigationModuleArchitectureTests
{
    private static readonly string[] Forbidden =
    [
        "LitigationModule.Store", "ModuleCase", "ModuleSubject", "ModuleScope", "ModuleScopeCase", "ModuleBridgeOutbox", "ModuleCaseObservation", "ModuleCaseLive", "ModuleRequestLink", "ModuleCaseMerge", "ModuleCaseMergeChange", "ModuleFetchWorkItem", "ModuleFetchWorkItemRun", "ModuleRefreshRun", "ModuleHostTrafficState", "BridgeEventKind", "LitigationModuleModel", "LitigationModuleStore"
    ];

    private static string AppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MCAROC_Analysis", "MCAROC_Analysis.csproj"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "MCAROC_Analysis");
    }

    [Fact]
    public void No_code_outside_the_module_references_the_modules_entities()
    {
        var root = AppRoot();
        var moduleDir = Path.Combine(root, "Services", "LitigationModule") + Path.DirectorySeparatorChar;
        var migrationsDir = Path.Combine(root, "Migrations") + Path.DirectorySeparatorChar;
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            if (file.StartsWith(moduleDir, StringComparison.OrdinalIgnoreCase) || file.StartsWith(migrationsDir, StringComparison.OrdinalIgnoreCase)) continue;
            var isDbContext = Path.GetFileName(file) == "AppDbContext.cs";
            foreach (var (line, n) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
            {
                // AppDbContext may call the model hook, and only that.
                if (isDbContext && line.Contains("LitigationModuleModel.Configure(")) continue;
                if (Forbidden.Any(f => ContainsWord(line, f))) violations.Add($"{Path.GetRelativePath(root, file)}:{n}: {line.Trim()}");
            }
        }
        Assert.True(violations.Count == 0, "Code outside Services/LitigationModule references the module's entities:\n" + string.Join("\n", violations));
    }

    private static bool ContainsWord(string line, string word)
    {
        var i = line.IndexOf(word, StringComparison.Ordinal);
        while (i >= 0)
        {
            var before = i == 0 || !(char.IsLetterOrDigit(line[i - 1]) || line[i - 1] == '_');
            var end = i + word.Length;
            var after = end >= line.Length || !(char.IsLetterOrDigit(line[end]) || line[end] == '_');
            if (before && after) return true;
            i = line.IndexOf(word, i + 1, StringComparison.Ordinal);
        }
        return false;
    }
}
