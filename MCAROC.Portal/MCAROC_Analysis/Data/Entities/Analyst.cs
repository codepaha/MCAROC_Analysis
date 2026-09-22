namespace MCAROC_Analysis.Data.Entities;

/// <summary>
/// A portal-managed analyst identity. It is deliberately separate from the configuration-backed
/// InternalReviewer credential: this record is the durable subject used for request assignments and audit.
/// </summary>
public sealed class Analyst
{
    public long AnalystId { get; set; }

    /// <summary>Operator-selected sign-in name. Stored in normalized form for case-insensitive lookup.</summary>
    public string LoginName { get; set; } = string.Empty;

    /// <summary>ASP.NET Core Identity PasswordHasher output only; never a password.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Short operator-facing label. No email address or other unnecessary identity data is required.</summary>
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedUtc { get; set; }
    public DateTime? DisabledUtc { get; set; }

    public List<AnalystAssignment> Assignments { get; set; } = [];
}
