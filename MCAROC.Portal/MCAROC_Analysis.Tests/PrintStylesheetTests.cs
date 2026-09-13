using Xunit;

namespace MCAROC_Analysis.Tests;

public class PrintStylesheetTests
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

    private static string ReadFile(params string[] pathSegments)
    {
        var repoRoot = FindRepoRoot();
        var fullPath = Path.Combine([repoRoot, .. pathSegments]);
        return File.ReadAllText(fullPath);
    }

    [Fact]
    public void Details_view_renders_outer_tab_content_with_stable_id()
    {
        var content = ReadFile("MCAROC.Portal", "MCAROC_Analysis", "Views", "Requests", "Details.cshtml");
        Assert.Contains("id=\"mcaMainTabContent\"", content);
    }

    [Fact]
    public void FinancialStatement_view_renders_basis_print_headings()
    {
        var content = ReadFile("MCAROC.Portal", "MCAROC_Analysis", "Views", "Requests", "Details", "_FinancialStatement.cshtml");
        Assert.Contains("<h5 class=\"mca-basis-print-heading d-none d-print-block\">Standalone Basis</h5>", content);
        Assert.Contains("<h5 class=\"mca-basis-print-heading d-none d-print-block\">Consolidated Basis</h5>", content);
    }

    [Fact]
    public void ComplianceTab_view_renders_basis_print_headings()
    {
        var content = ReadFile("MCAROC.Portal", "MCAROC_Analysis", "Views", "Requests", "Details", "_ComplianceTab.cshtml");
        Assert.Contains("<h5 class=\"mca-basis-print-heading d-none d-print-block\">Standalone Basis</h5>", content);
        Assert.Contains("<h5 class=\"mca-basis-print-heading d-none d-print-block\">Consolidated Basis</h5>", content);
    }

    [Fact]
    public void Stylesheet_contains_print_media_query_with_chrome_suppression()
    {
        var css = ReadFile("MCAROC.Portal", "MCAROC_Analysis", "wwwroot", "css", "mcaroc.css");

        Assert.Contains("@media print", css);
        Assert.Contains(".pi-sidebar", css);
        Assert.Contains(".pi-topbar", css);
        Assert.Contains(".pi-cmd", css);
        Assert.Contains(".pi-breadcrumb", css);
        Assert.Contains(".mca-chat-trigger", css);
        Assert.Contains(".mca-chat-panel", css);
        Assert.Contains(".mca-contents-nav", css);
        Assert.Contains("#mcaTabs", css);
        Assert.Contains(".mca-unit-toggle", css);
        Assert.Contains("[data-basis-group]", css);

        // App shell grid collapse to prevent sidebar margin squeeze
        Assert.Contains(".pi-app-shell", css);
        Assert.Contains("display: block !important;", css);

        // Dark theme token reset
        Assert.Contains("[data-theme=\"dark\"]", css);
    }

    [Fact]
    public void Stylesheet_forces_visibility_and_charge_expansion_in_print()
    {
        var css = ReadFile("MCAROC.Portal", "MCAROC_Analysis", "wwwroot", "css", "mcaroc.css");

        // Force all panes visible
        Assert.Contains(".tab-content > .tab-pane", css);
        // Consolidated hidden panels displayed
        Assert.Contains("[data-basis-panel][hidden]", css);
        Assert.Contains("display: block !important;", css);
        // Print headings displayed
        Assert.Contains(".mca-basis-print-heading", css);
        // Collapsed charge holders expanded
        Assert.Contains("tbody.collapse", css);
        Assert.Contains("display: table-row-group !important;", css);
    }

    [Fact]
    public void Stylesheet_scopes_page_breaks_to_main_panes_only()
    {
        var css = ReadFile("MCAROC.Portal", "MCAROC_Analysis", "wwwroot", "css", "mcaroc.css");

        // Page break applied to direct children of outer tab content
        Assert.Contains("#mcaMainTabContent > .tab-pane", css);
        Assert.Contains("break-before: page;", css);

        // Page break avoided/reset for nested subtab panes
        Assert.Contains("#mcaMainTabContent .tab-pane .tab-pane", css);
        Assert.Contains("break-before: auto !important;", css);
    }
}
