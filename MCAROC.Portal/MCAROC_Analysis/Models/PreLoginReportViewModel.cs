using System.ComponentModel.DataAnnotations;

namespace MCAROC_Analysis.Models;

public enum PreLoginReportFormat
{
    Sbi,
    Prr
}

public enum PreLoginReportEntityType
{
    Company,
    Llp,
    Partnership
}

public sealed class PreLoginReportViewModel : IValidatableObject
{
    [Display(Name = "CIN / LLPIN")]
    public string Cin { get; set; } = string.Empty;

    [Display(Name = "Company / LLP name (optional)")]
    [StringLength(250)]
    public string? CompanyName { get; set; }

    [Required]
    [Display(Name = "Report format")]
    public PreLoginReportFormat Format { get; set; } = PreLoginReportFormat.Sbi;

    [Display(Name = "Entity type")]
    public PreLoginReportEntityType EntityType { get; set; } = PreLoginReportEntityType.Company;

    [Display(Name = "Partnership name")]
    [StringLength(250)]
    public string? PartnershipName { get; set; }

    [Display(Name = "PAN / Registration Number")]
    [StringLength(100)]
    public string? PartnershipRegistrationNumber { get; set; }

    [Display(Name = "Address")]
    [StringLength(2000)]
    public string? PartnershipAddress { get; set; }

    public EditableLegalCasesViewModel LegalCases { get; set; } = new();

    [Display(Name = "Batch CINs")]
    [StringLength(5000)]
    public string? Cins { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (EntityType == PreLoginReportEntityType.Partnership)
        {
            if (Format != PreLoginReportFormat.Sbi)
                yield return new ValidationResult("Partnership reports are available in SBI format only.", [nameof(Format)]);
            if (string.IsNullOrWhiteSpace(PartnershipName))
                yield return new ValidationResult("Partnership name is required.", [nameof(PartnershipName)]);
            if (string.IsNullOrWhiteSpace(PartnershipRegistrationNumber))
                yield return new ValidationResult("PAN / Registration Number is required.", [nameof(PartnershipRegistrationNumber)]);
            if (string.IsNullOrWhiteSpace(PartnershipAddress))
                yield return new ValidationResult("Address is required.", [nameof(PartnershipAddress)]);
            yield break;
        }

        if (string.IsNullOrWhiteSpace(Cin))
            yield return new ValidationResult("CIN / LLPIN is required.", [nameof(Cin)]);
    }
}

public sealed class PreLoginReportDraftViewModel
{
    public long JobId { get; set; }
    /// <summary>The unguessable access token for this request (#47) — carried through the Edit round-trip
    /// so a re-rendered form (validation failure) keeps posting to the same batch-scoped route.</summary>
    public Guid BatchId { get; set; }
    [Required] public string Cin { get; set; } = string.Empty;
    [Required] public PreLoginReportFormat Format { get; set; }
    [Required] public EditableCompanyViewModel Company { get; set; } = new();
    public EditableLegalCasesViewModel? LegalCases { get; set; }
    public bool IsPartnership => LegalCases is not null;
    public List<EditableChargeViewModel> Charges { get; set; } = [];
    public List<EditableDirectorViewModel> Directors { get; set; } = [];
}

public sealed class EditableLegalCasesViewModel
{
    [Range(0, 9999)] public int SupremeCourt { get; set; }
    [Range(0, 9999)] public int HighCourt { get; set; }
    [Range(0, 9999)] public int DistrictCourt { get; set; }
    [Range(0, 9999)] public int ConsumerForum { get; set; }
    [Range(0, 9999)] public int ItatTax { get; set; }
    [Range(0, 9999)] public int NcltNclat { get; set; }
    [Range(0, 9999)] public int DrtDrat { get; set; }
    [Range(0, 9999)] public int Rera { get; set; }
    [Range(0, 9999)] public int NgtOthers { get; set; }
}

public sealed class EditableCompanyViewModel
{
    [Required, StringLength(250)] public string Name { get; set; } = string.Empty;
    [StringLength(250)] public string RocName { get; set; } = "-";
    [StringLength(100)] public string RegistrationNumber { get; set; } = "-";
    [StringLength(250)] public string Category { get; set; } = "-";
    [StringLength(250)] public string Subcategory { get; set; } = "-";
    [StringLength(100)] public string Class { get; set; } = "-";
    /// <summary>SBI-only field — the API has no corresponding data, so this is always manually entered.</summary>
    [StringLength(250)] public string ActiveCompliance { get; set; } = "-";
    [StringLength(100)] public string AuthorisedCapital { get; set; } = "-";
    [StringLength(100)] public string PaidUpCapital { get; set; } = "-";
    [StringLength(100)] public string Members { get; set; } = "-";
    [StringLength(100)] public string Incorporated { get; set; } = "-";
    [StringLength(2000)] public string Address { get; set; } = "-";
    /// <summary>SBI-only field — the API has no corresponding data, so this is always manually entered.</summary>
    [StringLength(2000)] public string BooksOfAccountAddress { get; set; } = "-";
    [StringLength(320)] public string Email { get; set; } = "-";
    [StringLength(100)] public string Listed { get; set; } = "-";
    [StringLength(100)] public string LastAgm { get; set; } = "-";
    [StringLength(100)] public string BalanceSheetDate { get; set; } = "-";
    [StringLength(250)] public string Status { get; set; } = "-";
}

public sealed class EditableChargeViewModel
{
    /// <summary>SBI-only field (the charge's MCA Service Request Number) — PRR's charges table doesn't show it.</summary>
    [StringLength(100)] public string Srn { get; set; } = "-";
    [StringLength(100)] public string Id { get; set; } = "-";
    [StringLength(500)] public string Holder { get; set; } = "-";
    [StringLength(100)] public string Created { get; set; } = "-";
    [StringLength(100)] public string Modified { get; set; } = "-";
    [StringLength(100)] public string Satisfied { get; set; } = "-";
    [StringLength(100)] public string Amount { get; set; } = "-";
    public bool IsOpen { get; set; }
}

public sealed class EditableDirectorViewModel
{
    [StringLength(250)] public string Name { get; set; } = "-";
    [StringLength(100)] public string DinOrPan { get; set; } = "-";
    [StringLength(250)] public string Designation { get; set; } = "-";
    [StringLength(100)] public string Appointed { get; set; } = "-";
}
