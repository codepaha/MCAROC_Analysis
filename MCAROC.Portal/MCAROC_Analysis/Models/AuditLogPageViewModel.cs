using System.Collections.Generic;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Models;

public class AuditLogPageViewModel
{
    public long RequestId { get; set; }
    public string RequestNumber { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public List<AuditLog> Items { get; set; } = [];
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public string? Filter { get; set; }
}
