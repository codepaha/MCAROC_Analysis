using System.ComponentModel.DataAnnotations;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Models;

/// <summary>The auto-fetch New-request form: everything the manual form asks for except the three file
/// uploads, which the reference tool supplies from the CIN/LLPIN.</summary>
public class AutoFetchRequestViewModel
{
    [Required]
    public long ClientId { get; set; }

    public List<Client> Clients { get; set; } = [];

    [Required]
    public EntityType EntityType { get; set; } = EntityType.Company;

    /// <summary>Optional — filled from the reference tool's search, or from the workbook once extracted.</summary>
    [Display(Name = "Company / LLP Name")]
    public string? CompanyName { get; set; }

    [Required]
    [Display(Name = "CIN / LLPIN")]
    public string? Cin { get; set; }

    public string? Pan { get; set; }

    [Display(Name = "Also fetch the filing PDFs")]
    public bool IncludeFilings { get; set; } = true;

    /// <summary>0 / blank = every document the reference tool lists in each of its four sections.</summary>
    [Display(Name = "Max documents per section")]
    [Range(0, 100_000)]
    public int? MaxDocumentsPerSection { get; set; }

    public bool IsConfigured { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>JSON shape the Details page polls while a job runs.</summary>
public sealed record AutoFetchStatusDto(
    long RequestId,
    string Status,
    bool IsTerminal,
    int ProgressPercent,
    string? StatusMessage,
    string? FailureReason,
    IReadOnlyList<string> Warnings,
    int FilesTotal,
    int FilesDownloaded,
    int FilesFailed,
    long BytesDownloaded,
    int RegistryTotalCount,
    int RegistryListedCount,
    string RequestStatus,
    DateTime? StartedUtc,
    DateTime? CompletedUtc);
