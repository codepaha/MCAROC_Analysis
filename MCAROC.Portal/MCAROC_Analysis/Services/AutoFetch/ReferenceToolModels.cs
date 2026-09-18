namespace MCAROC_Analysis.Services.AutoFetch;

/// <summary>Thrown for any reference-tool failure the job should surface to the user verbatim (session
/// expired, company not unlocked, non-Excel response…). <see cref="Retryable"/> marks transient HTTP
/// failures a per-file retry loop may re-attempt.</summary>
public sealed class ReferenceToolException(string message, bool retryable = false, Exception? inner = null) : Exception(message, inner)
{
    public bool Retryable { get; } = retryable;
}

/// <summary>One auto-complete hit from the reference tool's company search.</summary>
public sealed record ReferenceCompanyHint(string LegalName, string Cin, string? Bid, string? Status, string? CompanyType);

/// <summary>Result of the session check: whether the configured cookie is still a logged-in session,
/// and the tool's numeric user id when the response carried one.</summary>
public sealed record ReferenceSessionInfo(bool IsValid, string? UserId, string? Detail);

/// <summary>Which of the tool's two workbook exports to download.</summary>
public enum ReferenceWorkbookKind
{
    /// <summary>The corporate master workbook (every sheet) — our MCA / ROC report.</summary>
    Corporate,
    /// <summary>The charge-details workbook — our Detailed Charge Report.</summary>
    Charge
}

public sealed record ReferenceDocumentAttachment(string Name, string AwsPath);

/// <summary>One filing in the tool's reference-documents registry: the main e-form PDF plus its
/// attachments. <see cref="DocId"/> is the tool's stable id for the filing (hex + version suffix).</summary>
public sealed record ReferenceDocument(
    string DocId,
    string Name,
    string? FormName,
    DateTimeOffset? DocumentDate,
    double? SizeKb,
    string AwsPath,
    string? Section,
    IReadOnlyList<ReferenceDocumentAttachment> Attachments);

/// <summary>One of the registry's four sections. <see cref="TotalCount"/> is what the tool reports it
/// holds; <see cref="Documents"/> is what it actually listed (the two differ when the tool pages).</summary>
public sealed record ReferenceDocumentSection(string Key, string FolderName, int TotalCount, IReadOnlyList<ReferenceDocument> Documents);

public sealed record ReferenceDocumentRegistry(IReadOnlyList<ReferenceDocumentSection> Sections)
{
    public int ListedCount => Sections.Sum(s => s.Documents.Count);
    public int TotalCount => Sections.Sum(s => s.TotalCount);
}
