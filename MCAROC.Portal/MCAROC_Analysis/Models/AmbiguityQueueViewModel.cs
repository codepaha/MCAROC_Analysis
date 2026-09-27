using MCAROC_Analysis.Services.CompanyMaster;

namespace MCAROC_Analysis.Models;

/// <summary>View model for the ambiguity queue board (issue #295, plan §5A.3).</summary>
public sealed class AmbiguityQueueViewModel
{
    public List<AmbiguityQueueItemViewModel> Items { get; set; } = [];
    public long? FilterRequestId { get; set; }
}

public sealed class AmbiguityQueueItemViewModel
{
    public long RequestId { get; set; }
    public string RequestNumber { get; set; } = string.Empty;
    public string? ClientName { get; set; }
    public string? InputName { get; set; }
    public string? CurrentCin { get; set; }
    public ResolutionHints Hints { get; set; } = ResolutionHints.None;
    public string ReasonCode { get; set; } = string.Empty;
    public string? ReasonDetail { get; set; }
    public string? RecommendedIdentifier { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public List<AmbiguityCandidateViewModel> Candidates { get; set; } = [];
}

public sealed class AmbiguityCandidateViewModel
{
    public string Identifier { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? RecordType { get; set; }
    public string? Status { get; set; }
    public string? State { get; set; }
    public string? District { get; set; }
    public DateOnly? RegistrationDate { get; set; }
    public string? Category { get; set; }
    public string? Class { get; set; }
    public string? ListingStatus { get; set; }
    public bool ToolOnly { get; set; }
    public double Score { get; set; }
    public int MatchPercent => (int)Math.Round(Score * 100);
    public IReadOnlyList<string> Reasons { get; set; } = [];
    public bool IsRecommended { get; set; }
}
