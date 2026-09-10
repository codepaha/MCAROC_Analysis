namespace MCAROC_Analysis.Services.Dossier;

/// <summary>Visual constants for the client "Due Diligence Dossier" PDF (QuestPDF). The colour values
/// mirror <c>wwwroot/css/dossier-tokens.css</c> exactly — <c>DossierThemeSyncTests</c> fails CI if the
/// two drift, so the client PDF and the portal restyle can never diverge silently.</summary>
public static class DossierTheme
{
    // ── Colours (kept in sync with dossier-tokens.css :root) ──
    public const string Paper       = "#F6F3EC";
    public const string PaperRaised = "#FCFAF5";
    public const string Ink         = "#1B2430";
    public const string InkSoft     = "#5B6472";
    public const string InkFaint    = "#8B93A0";
    public const string Maroon      = "#7A2331";
    public const string MaroonDeep  = "#5C1A23";
    public const string MaroonWash  = "#F4E6E8";
    public const string Sage        = "#3F6B52";
    public const string SageWash    = "#E7EFE9";
    public const string Amber       = "#A66A1E";
    public const string AmberWash   = "#F5E9D8";
    public const string Line        = "#C4C0B4";
    public const string LineSoft    = "#E4E1D6";

    // ── Fonts (registered from wwwroot/fonts by DossierFonts.Register) — PDF-only, not sync-tested ──
    public const string Display = "Fraunces";        // headings, kickers, narrative prose
    public const string Sans    = "IBM Plex Sans";   // body + UI text
    public const string Mono    = "IBM Plex Mono";   // CIN / PAN / DIN / ₹ amounts / dates

    // ── Type scale (pt) ──
    public const float CoverTitle   = 30f;
    public const float SectionTitle = 22f;
    public const float Heading      = 13.5f;
    public const float Kicker       = 8.5f;
    public const float Body         = 9.5f;
    public const float Small        = 8f;
    public const float TableHeader  = 7.5f;
    public const float TableCell    = 8f;

    // ── Geometry ──
    public const float PageMarginCm = 2.2f;
    public const float Radius       = 3f;
}
