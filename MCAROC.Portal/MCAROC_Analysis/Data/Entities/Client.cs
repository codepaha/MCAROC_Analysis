namespace MCAROC_Analysis.Data.Entities;

public class Client
{
    public long ClientId { get; set; }
    public string ClientCode { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string? ContactPerson { get; set; }
    public string? ContactEmail { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedDate { get; set; }
    public DateTime? UpdatedDate { get; set; }

    /// <summary>When false, this client's dossier PDF omits Litigation entirely — annexure, TOC entry,
    /// every litigation-sourced finding/flag, and the AI executive summary (which can't be safely filtered
    /// for litigation mentions). Used when a client already receives litigation as a separate standalone
    /// report. Portal-only views (the on-screen company page) are unaffected — this only gates
    /// DossierPdfComposer's PDF output, never the shared DossierModel/DossierAssembler data. Default true
    /// so every existing client keeps today's behavior.</summary>
    public bool IncludeLitigationInDossier { get; set; } = true;
}
