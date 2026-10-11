namespace MCAROC_Analysis.Services.LitigationModule.Sources.Orders;

/// <summary>Source adapter interface for downloading litigation court orders across portals (plan section 4.5).</summary>
public interface IOrderSourceClient
{
    /// <summary>Whether this adapter can handle the given order source locator.</summary>
    bool CanHandle(OrderSourceLocator locator);

    /// <summary>Downloads and cleans the order document.</summary>
    /// <param name="locator">The locator describing the order.</param>
    /// <param name="maxBytes">Maximum allowable download payload size in bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<OrderSourceDownloadResult> DownloadAsync(
        OrderSourceLocator locator,
        long maxBytes = 20 * 1024 * 1024,
        CancellationToken ct = default);
}

