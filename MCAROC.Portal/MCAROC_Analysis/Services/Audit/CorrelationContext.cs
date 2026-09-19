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
        if (httpContext.Request.Headers.TryGetValue(HeaderName, out var headerVal) && !string.IsNullOrWhiteSpace(headerVal))
        {
            cid = headerVal.ToString().Trim();
            if (cid.Length > 64) cid = cid[..64];
        }
        else if (!string.IsNullOrWhiteSpace(httpContext.TraceIdentifier))
        {
            cid = httpContext.TraceIdentifier.Trim();
            if (cid.Length > 64) cid = cid[..64];
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
