using System.ComponentModel.DataAnnotations;

namespace MCAROC_Analysis.Models;

public sealed class ClientEditViewModel
{
    public long ClientId { get; set; }
    [Display(Name = "Client name")] public string ClientName { get; set; } = string.Empty;
    [Display(Name = "Client code")] public string ClientCode { get; set; } = string.Empty;

    [Display(Name = "Include Litigation in the dossier PDF")]
    public bool IncludeLitigationInDossier { get; set; } = true;
}
