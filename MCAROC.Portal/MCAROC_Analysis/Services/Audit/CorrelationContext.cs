using Microsoft.AspNetCore.Http;

namespace MCAROC_Analysis.Services.Audit;

public static class CorrelationContext
{
    public const string HeaderName = "X-Correlation-ID";
    public const string ItemKey = "__MCAROC_CorrelationId";

    public static string GetOrCreate(HttpContext? httpContext)
    {
        if (httpContext is null)
            return GenerateCorrelationId();

        if (httpContext.Items.TryGetValue(ItemKey, out var existing) && existing is string s && !string.IsNullOrWhiteSpace(s))
            return s;

        string cid;
        // Validate client-supplied correlation header as a strict bounded trace identifier (valid Guid),
        // or generate the durable identifier server-side to prevent trace collisions and spoofing.
        if (httpContext.Request.Headers.TryGetValue(HeaderName, out var headerVal)
            && Guid.TryParse(headerVal.ToString(), out var clientGuid))
        {
            cid = clientGuid.ToString("N");
        }
        else
        {
            cid = GenerateCorrelationId();
        }

        httpContext.Items[ItemKey] = cid;
        return cid;
    }

    public static string GenerateCorrelationId() =>
        Guid.NewGuid().ToString("N");
}
