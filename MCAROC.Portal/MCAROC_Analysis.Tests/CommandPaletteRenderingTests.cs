using Xunit;

namespace MCAROC_Analysis.Tests;

public class CommandPaletteRenderingTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new DirectoryNotFoundException("MCAROC.Portal not found in parent hierarchy");
    }

    private static string ReadLayoutContent()
    {
        var repoRoot = FindRepoRoot();
        var layoutPath = Path.Combine(repoRoot, "MCAROC.Portal", "MCAROC_Analysis", "Views", "Shared", "_Layout.cshtml");
        return File.ReadAllText(layoutPath);
    }

    [Fact]
    public void Layout_renders_command_palette_trigger_with_aria_attributes()
    {
        var content = ReadLayoutContent();

        Assert.Contains("id=\"piCmdBtn\"", content);
        Assert.Contains("aria-label=\"Open command palette\"", content);
        Assert.Contains("aria-haspopup=\"dialog\"", content);
        Assert.Contains("aria-expanded=\"false\"", content);
        Assert.Contains("aria-controls=\"piCmdModal\"", content);
    }

    [Fact]
    public void Layout_renders_command_palette_overlay_hidden_initially()
    {
        var content = ReadLayoutContent();

        Assert.Contains("id=\"piCmdOverlay\"", content);
        Assert.Contains("role=\"dialog\"", content);
        Assert.Contains("aria-modal=\"true\"", content);
        Assert.Contains("aria-label=\"Command palette\"", content);
        Assert.Contains("hidden", content);
    }

    [Fact]
    public void Layout_renders_command_palette_input_with_combobox_contract()
    {
        var content = ReadLayoutContent();

        Assert.Contains("id=\"piCmdInput\"", content);
        Assert.Contains("role=\"combobox\"", content);
        Assert.Contains("aria-autocomplete=\"list\"", content);
        Assert.Contains("aria-expanded=\"false\"", content);
        Assert.Contains("aria-controls=\"piCmdHints\"", content);
    }

    [Fact]
    public void Layout_renders_command_palette_hints_listbox()
    {
        var content = ReadLayoutContent();

        Assert.Contains("id=\"piCmdHints\"", content);
        Assert.Contains("role=\"listbox\"", content);
        Assert.Contains("aria-label=\"Suggested commands\"", content);
    }

    [Fact]
    public void Layout_includes_command_palette_script_reference()
    {
        var content = ReadLayoutContent();

        Assert.Contains("src=\"~/js/command-palette.js\"", content);
        Assert.Contains("asp-append-version=\"true\"", content);
    }
}
