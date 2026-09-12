using System.Reflection;

namespace MCAROC_Analysis.Models;

/// <summary>A real build identifier for the footer — the git commit the running binary was built from,
/// embedded automatically by Microsoft.Build.Tasks.Git into AssemblyInformationalVersion (e.g.
/// "1.0.0+bc33619cbebea2c652f78601fc46ed40dc7b1f96"). Falls back to the bare assembly version when no
/// git metadata was available at build time (e.g. a source tree with no .git folder).</summary>
public static class BuildInfo
{
    public static string Version { get; } = Compute();

    private static string Compute()
    {
        var assembly = Assembly.GetEntryAssembly();
        var informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var plainVersion = assembly?.GetName().Version?.ToString(3) ?? "dev";

        if (string.IsNullOrWhiteSpace(informational)) return plainVersion;

        var plusIndex = informational.IndexOf('+');
        if (plusIndex < 0 || plusIndex == informational.Length - 1) return plainVersion;

        var sha = informational[(plusIndex + 1)..];
        var shortSha = sha.Length > 7 ? sha[..7] : sha;
        return $"{informational[..plusIndex]}+{shortSha}";
    }
}
