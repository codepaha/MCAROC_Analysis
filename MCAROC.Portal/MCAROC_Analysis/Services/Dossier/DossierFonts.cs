using QuestPDF.Drawing;
using QuestPDF.Infrastructure;

namespace MCAROC_Analysis.Services.Dossier;

/// <summary>Registers the bundled OFL font files (<c>wwwroot/fonts/</c>) with QuestPDF once at startup so
/// the dossier renders identically on any machine, with or without the fonts installed system-wide.
/// Fraunces cuts are static instances of the variable font (wght 400/500/600, opsz set for display).</summary>
public static class DossierFonts
{
    private static bool _registered;
    private static readonly object Gate = new();

    public static void Register(string webRootPath)
    {
        if (_registered) return;
        lock (Gate)
        {
            if (_registered) return;

            // QuestPDF Community licence — Cubictree's gross revenue is under the US$1M threshold.
            QuestPDF.Settings.License = LicenseType.Community;

            var dir = Path.Combine(webRootPath, "fonts");
            if (Directory.Exists(dir))
            {
                foreach (var ttf in Directory.EnumerateFiles(dir, "*.ttf"))
                {
                    using var stream = File.OpenRead(ttf);
                    FontManager.RegisterFont(stream);
                }
            }
            _registered = true;
        }
    }
}
