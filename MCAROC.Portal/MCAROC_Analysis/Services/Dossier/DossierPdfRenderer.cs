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
        return new DossierPdfComposer(model, variant).GeneratePdf();
    }
}
