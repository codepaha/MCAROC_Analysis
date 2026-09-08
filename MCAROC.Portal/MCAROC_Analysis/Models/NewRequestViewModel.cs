using System.ComponentModel.DataAnnotations;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Models;

public class NewRequestViewModel
{
    [Required]
    public long ClientId { get; set; }

    public List<Client> Clients { get; set; } = [];

    [Required]
    public EntityType EntityType { get; set; } = EntityType.Company;

    [Required]
    [Display(Name = "Company / LLP Name")]
    public string CompanyName { get; set; } = string.Empty;

    [Display(Name = "CIN / LLPIN")]
    public string? Cin { get; set; }

    public string? Pan { get; set; }

    [Required]
    [Display(Name = "MCA / ROC Report")]
    public IFormFile? RocFile { get; set; }

    [Display(Name = "Detailed Charge Report")]
    public IFormFile? ChargeFile { get; set; }

    [Display(Name = "MCA Filings Archive (.zip)")]
    public IFormFile? McaFilingsFile { get; set; }

    public string? ErrorMessage { get; set; }
}
