namespace MCAROC_Analysis.Services.LitigationModule.Orders;

/// <summary>An order item participating in cross-source equivalence checks (plan section 4.5).</summary>
/// <param name="OrderId">Unique identifier of the order row.</param>
/// <param name="Source">The originating source (e.g. "Bpr", "Drt", "Consumer").</param>
/// <param name="OrderKey">The stable per-source order key.</param>
/// <param name="CaseId">The identifier of the case holding this order.</param>
/// <param name="IsCaseVerified">Whether the case is verified for the request.</param>
/// <param name="OrderDate">The date the order was pronounced/made.</param>
/// <param name="ContentHash">Cryptographic hash of the downloaded payload (SHA-256 of bytes or normalized HTML text).</param>
/// <param name="HasExtractedText">Whether extractable text has been obtained for this order document.</param>
public sealed record OrderComparisonItem(
    long OrderId,
    string Source,
    string OrderKey,
    long CaseId,
    bool IsCaseVerified,
    DateOnly? OrderDate,
    string? ContentHash,
    bool HasExtractedText);

/// <summary>The outcome of comparing two orders for equivalence.</summary>
public enum OrderEquivalenceOutcome
{
    /// <summary>The orders represent distinct proceedings (different cases, different dates, unverified cases, or differing content hashes).</summary>
    Distinct,

    /// <summary>The orders belong to the same verified case and share an order date, but at least one order has not yet been downloaded/hashed.</summary>
    PossibleDuplicate,

    /// <summary>The orders belong to the same verified case, share an order date, and have identical content hashes.</summary>
    Equivalent
}

/// <summary>The verdict of an order equivalence evaluation.</summary>
public sealed record OrderEquivalenceResult(
    OrderEquivalenceOutcome Outcome,
    string Reason,
    long? SurvivorOrderId = null,
    long? CollapsedOrderId = null)
{
    public bool IsEquivalent => Outcome == OrderEquivalenceOutcome.Equivalent;
    public bool IsPossibleDuplicate => Outcome == OrderEquivalenceOutcome.PossibleDuplicate;
}

/// <summary>Determines cross-source order equivalence and duplicate collapsing (plan section 4.5).</summary>
public static class OrderEquivalence
{
    /// <summary>Evaluates whether two orders are equivalent, possible duplicates, or distinct.</summary>
    public static OrderEquivalenceResult Evaluate(OrderComparisonItem a, OrderComparisonItem b)
    {
        // 1. Both orders must resolve to the same verified case
        if (a.CaseId != b.CaseId)
        {
            return new(OrderEquivalenceOutcome.Distinct, "Orders belong to different cases.");
        }

        if (!a.IsCaseVerified || !b.IsCaseVerified)
        {
            return new(OrderEquivalenceOutcome.Distinct, "Orders belong to unverified cases; cross-source equivalence requires verified cases.");
        }

        // 2. Both orders must share the same order date
        if (a.OrderDate is null || b.OrderDate is null || a.OrderDate != b.OrderDate)
        {
            return new(OrderEquivalenceOutcome.Distinct, $"Order dates do not match ({a.OrderDate} vs {b.OrderDate}).");
        }

        // 3. Check downloaded content hashes
        var hashA = a.ContentHash?.Trim();
        var hashB = b.ContentHash?.Trim();

        // If either content has not yet been downloaded / hashed, flag as possible duplicate but keep separate
        if (string.IsNullOrEmpty(hashA) || string.IsNullOrEmpty(hashB))
        {
            return new(
                OrderEquivalenceOutcome.PossibleDuplicate,
                $"Both orders belong to verified case {a.CaseId} on {a.OrderDate}, but content hashes are not yet available for comparison.");
        }

        // Matching path, matching date, or similar names never decide it without content hash match
        if (!string.Equals(hashA, hashB, StringComparison.OrdinalIgnoreCase))
        {
            return new(
                OrderEquivalenceOutcome.Distinct,
                $"Order content hashes differ ({hashA} vs {hashB}); separate orders retained.");
        }

        // Equivalent: identical content hash on the same verified case and date.
        // Survivor rule: the one with extracted text survives; if tie, the lower/earlier order ID survives.
        long survivorId;
        long collapsedId;

        if (a.HasExtractedText && !b.HasExtractedText)
        {
            survivorId = a.OrderId;
            collapsedId = b.OrderId;
        }
        else if (!a.HasExtractedText && b.HasExtractedText)
        {
            survivorId = b.OrderId;
            collapsedId = a.OrderId;
        }
        else
        {
            // Both have or neither has extracted text: stable tie-break by order ID
            if (a.OrderId <= b.OrderId)
            {
                survivorId = a.OrderId;
                collapsedId = b.OrderId;
            }
            else
            {
                survivorId = b.OrderId;
                collapsedId = a.OrderId;
            }
        }

        return new(
            OrderEquivalenceOutcome.Equivalent,
            $"Orders are equivalent with identical content hash on verified case {a.CaseId} on {a.OrderDate}.",
            SurvivorOrderId: survivorId,
            CollapsedOrderId: collapsedId);
    }
}

