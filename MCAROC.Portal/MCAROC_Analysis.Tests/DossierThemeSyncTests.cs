using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Tests;

/// <summary>Tripwire: the client PDF's colours (<c>Services/Dossier/DossierTheme.cs</c>) and the portal's
/// editorial palette (<c>wwwroot/css/dossier-tokens.css</c>) must stay identical. If a future design tweak
/// changes one and not the other, the client-facing PDF and the on-screen portal drift — this fails CI
/// instead of shipping that.</summary>
public class DossierThemeSyncTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !(Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal")) &&
                 File.Exists(Path.Combine(dir.FullName, ".gitignore"))))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    private static Dictionary<string, string> CssTokens(string css) =>
        Regex.Matches(css, @"--([a-z][a-z0-9-]*)\s*:\s*(#[0-9A-Fa-f]{3,8})\b")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.ToUpperInvariant());

    private static Dictionary<string, string> ThemeColours(string cs) =>
        Regex.Matches(cs, @"public const string (\w+)\s*=\s*""(#[0-9A-Fa-f]{3,8})""")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.ToUpperInvariant());

    private static string Pascal(string kebab) =>
        string.Concat(kebab.Split('-').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    [Fact]
    public void DossierTheme_colours_match_the_shared_css_palette()
    {
        var css = CssTokens(Read("MCAROC.Portal", "MCAROC_Analysis", "wwwroot", "css", "dossier-tokens.css"));
        var theme = ThemeColours(Read("MCAROC.Portal", "MCAROC_Analysis", "Services", "Dossier", "DossierTheme.cs"));

        Assert.NotEmpty(css);
        Assert.NotEmpty(theme);

        // Every CSS token must have an identical DossierTheme constant.
        foreach (var (name, hex) in css)
        {
            var pascal = Pascal(name);
            Assert.True(theme.ContainsKey(pascal), $"DossierTheme.cs is missing '{pascal}' (css token --{name})");
            Assert.Equal(hex, theme[pascal]);
        }

        // Every DossierTheme colour constant must trace back to a CSS token (no PDF-only colours).
        var cssPascal = css.Keys.Select(Pascal).ToHashSet();
        foreach (var name in theme.Keys)
            Assert.True(cssPascal.Contains(name), $"DossierTheme.{name} has no matching token in dossier-tokens.css");
    }
}
