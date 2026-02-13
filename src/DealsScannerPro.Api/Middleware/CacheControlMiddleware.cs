using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;

namespace DealsScannerPro.Api.Middleware;

/// <summary>
/// Sets Cache-Control headers on successful GET responses based on route.
/// </summary>
public class CacheControlMiddleware : IFunctionsWorkerMiddleware
{
    private static readonly (string RouteContains, string CacheControl)[] CacheRules =
    [
        ("api/stores", "public, max-age=3600"),
        ("api/categories", "public, max-age=3600"),
        ("api/deals", "public, max-age=300"),
        ("api/tilbud/search", "public, max-age=300"),
    ];

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        await next(context);

        var httpRequestData = await context.GetHttpRequestDataAsync();
        if (httpRequestData == null) return;

        // Only apply to GET requests
        if (!string.Equals(httpRequestData.Method, "GET", StringComparison.OrdinalIgnoreCase))
            return;

        var result = context.GetInvocationResult();
        if (result.Value is not HttpResponseData response) return;

        // Only apply to successful responses (2xx)
        var statusCode = (int)response.StatusCode;
        if (statusCode < 200 || statusCode >= 300) return;

        var url = httpRequestData.Url.PathAndQuery.ToLowerInvariant();

        foreach (var (routeContains, cacheControl) in CacheRules)
        {
            if (url.Contains(routeContains))
            {
                response.Headers.Add("Cache-Control", cacheControl);
                return;
            }
        }
    }
}
