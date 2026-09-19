using System;
using System.Collections.Generic;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CompanyMaster;

namespace MCAROC_Analysis.Models;

public sealed class CompanyMasterDashboardViewModel
{
    public int TotalCompanies { get; set; }
    public int TotalLlps { get; set; }
    public int TotalForeign { get; set; }
    public int TotalRecords => TotalCompanies + TotalLlps + TotalForeign;

    public DateOnly? LastSnapshotDate { get; set; }
    public DateTime? LastSyncCompletedUtc { get; set; }
    public string? LatestStatus { get; set; }

    public IReadOnlyList<CompanyMasterSyncJob> RecentJobs { get; set; } = Array.Empty<CompanyMasterSyncJob>();
    public IReadOnlyList<ProxyNode> ProxyNodes { get; set; } = Array.Empty<ProxyNode>();

    public string? Message { get; set; }
    public string? ErrorMessage { get; set; }
}
