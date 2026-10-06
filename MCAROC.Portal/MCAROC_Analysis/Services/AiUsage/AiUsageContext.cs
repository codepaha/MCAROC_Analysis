namespace MCAROC_Analysis.Services.AiUsage;

/// <summary>The assignment (and optionally the purpose) the model calls made on the current logical thread belong to. Callers that already know the
/// request set it once around their work; the clients that make the calls need no extra argument, so they stay unchanged for their other callers.</summary>
public static class AiUsageContext
{
    private static readonly AsyncLocal<(long? RequestId, string? Purpose)?> Current = new();

    public static long? RequestId => Current.Value?.RequestId;
    public static string? Purpose => Current.Value?.Purpose;

    /// <summary>Marks the calls made until the returned scope is disposed. A scope without a purpose keeps the enclosing one.</summary>
    public static IDisposable Begin(long? requestId, string? purpose = null)
    {
        var previous = Current.Value;
        Current.Value = (requestId ?? previous?.RequestId, purpose ?? previous?.Purpose);
        return new Scope(previous);
    }

    private sealed class Scope((long? RequestId, string? Purpose)? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
