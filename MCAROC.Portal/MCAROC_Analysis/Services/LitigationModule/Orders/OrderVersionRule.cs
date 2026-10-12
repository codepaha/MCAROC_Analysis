namespace MCAROC_Analysis.Services.LitigationModule.Orders;

/// <summary>The outcome of checking an order download for document versioning (plan section 4.5).</summary>
public enum OrderVersionDecisionType
{
    /// <summary>Initial document version for this order identity.</summary>
    Initial,

    /// <summary>The newly downloaded content matches the current document version; no change needed.</summary>
    Identical,

    /// <summary>The content has changed under the same order identity; creates a new version while marking the older document superseded.</summary>
    Superseded
}

/// <summary>The result of evaluating order versioning rules.</summary>
/// <param name="Decision">The decision type.</param>
/// <param name="VersionNumber">The version number to assign to the new document.</param>
/// <param name="PreviousMarkedSuperseded">True if the previous document version must be marked superseded (never overwritten).</param>
/// <param name="Reason">Human-readable audit explanation.</param>
public sealed record OrderVersionDecision(
    OrderVersionDecisionType Decision,
    int VersionNumber,
    bool PreviousMarkedSuperseded,
    string Reason);

/// <summary>Evaluates document versioning when downloading court orders under a stable identity (plan section 4.5).</summary>
public static class OrderVersionRule
{
    /// <summary>Evaluates whether a newly downloaded content payload represents an initial version, an identical duplicate,
    /// or a new version superseding the previous document.</summary>
    /// <param name="currentContentHash">Content hash of the currently stored document, if any.</param>
    /// <param name="currentVersion">Version number of the current document (typically 1 for initial).</param>
    /// <param name="newContentHash">Content hash of the newly downloaded payload.</param>
    public static OrderVersionDecision Evaluate(
        string? currentContentHash,
        int currentVersion,
        string newContentHash)
    {
        if (string.IsNullOrWhiteSpace(newContentHash))
        {
            throw new ArgumentException("New content hash cannot be null or empty.", nameof(newContentHash));
        }

        var curHash = currentContentHash?.Trim();
        var incomingHash = newContentHash.Trim();

        // If no prior document exists
        if (string.IsNullOrEmpty(curHash))
        {
            return new(
                OrderVersionDecisionType.Initial,
                VersionNumber: Math.Max(1, currentVersion),
                PreviousMarkedSuperseded: false,
                Reason: "Initial document version.");
        }

        // If content is identical
        if (string.Equals(curHash, incomingHash, StringComparison.OrdinalIgnoreCase))
        {
            return new(
                OrderVersionDecisionType.Identical,
                VersionNumber: currentVersion,
                PreviousMarkedSuperseded: false,
                Reason: "Downloaded content is identical to the current document version; retained unchanged.");
        }

        // Content changed under the same order identity: new version created, previous marked superseded
        var nextVersion = (currentVersion <= 0 ? 1 : currentVersion) + 1;
        return new(
            OrderVersionDecisionType.Superseded,
            VersionNumber: nextVersion,
            PreviousMarkedSuperseded: true,
            Reason: $"Content changed under the same identity ({curHash} -> {incomingHash}); creating version {nextVersion} and marking version {currentVersion} superseded.");
    }
}

