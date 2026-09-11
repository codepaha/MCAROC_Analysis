using System.ComponentModel.DataAnnotations;

namespace MCAROC_Analysis.Models;

public enum PreLoginReportFormat
{
    Sbi,
    Prr
}

public sealed class PreLoginReportViewModel
{
    [Required(ErrorMessage = "CIN is required.")]
    [Display(Name = "Company CIN")]
    [RegularExpression("^[LU][0-9]{5}[A-Z]{2}[0-9]{4}[A-Z]{3}[0-9]{6}$",
        ErrorMessage = "Enter a valid company CIN, for example U74899DL1991PTC043274. LLPINs are not supported by this API.")]
    public string Cin { get; set; } = string.Empty;

    [Display(Name = "Company / LLP name (optional)")]
    [StringLength(250)]
    public string? CompanyName { get; set; }

    [Required]
    [Display(Name = "Report format")]
    public PreLoginReportFormat Format { get; set; } = PreLoginReportFormat.Sbi;

    [Display(Name = "Batch CINs")]
    [StringLength(5000)]
    public string? Cins { get; set; }
}

public sealed class PreLoginReportDraftViewModel
{
    public long JobId { get; set; }
    [Required] public string Cin { get; set; } = string.Empty;
    [Required] public PreLoginReportFormat Format { get; set; }
    [Required] public EditableCompanyViewModel Company { get; set; } = new();
    public List<EditableChargeViewModel> Charges { get; set; } = [];
    public List<EditableDirectorViewModel> Directors { get; set; } = [];
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
