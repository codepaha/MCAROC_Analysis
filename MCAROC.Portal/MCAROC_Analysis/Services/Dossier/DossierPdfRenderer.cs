using MCAROC_Analysis.Models.Dossier;
using QuestPDF.Fluent;

namespace MCAROC_Analysis.Services.Dossier;

/// <summary>Renders a <see cref="DossierModel"/> to PDF bytes. Font registration is one-time and idempotent.</summary>
public class DossierPdfRenderer
{
    private readonly string _webRootPath;

    public DossierPdfRenderer(IWebHostEnvironment env) : this(env.WebRootPath) { }

    internal DossierPdfRenderer(string webRootPath) => _webRootPath = webRootPath;

    public byte[] Render(DossierModel model, DossierVariant variant)
    {
        DossierFonts.Register(_webRootPath);
        return new DossierPdfComposer(model, variant, TryLoadLogo()).GeneratePdf();
    }

    /// <summary>The Cubictree mark for the cover, from <c>wwwroot/images/favicon.png</c>. A missing file
    /// is not fatal — the composer falls back to the maroon "CT" text badge.</summary>
    private byte[]? TryLoadLogo()
    {
        try
        {
            var path = Path.Combine(_webRootPath, "images", "favicon.png");
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
