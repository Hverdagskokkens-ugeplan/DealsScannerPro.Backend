using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace DealsScannerPro.Api.Middleware;

/// <summary>
/// IP-based sliding window rate limiter for Azure Functions.
/// Limits each IP to 100 requests per minute.
/// Note: This is a per-instance limit — when scaled out, each instance tracks independently.
/// </summary>
public class RateLimitingMiddleware : IFunctionsWorkerMiddleware
{
    private static readonly ConcurrentDictionary<string, Queue<DateTime>> _requestLog = new();
    private const int MaxRequests = 100;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static DateTime _lastCleanup = DateTime.UtcNow;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private static readonly object _cleanupLock = new();

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        // Skip non-HTTP triggers (timer functions etc.)
        var httpRequestData = await context.GetHttpRequestDataAsync();
        if (httpRequestData == null)
        {
            await next(context);
            return;
        }

        var clientIp = GetClientIp(httpRequestData);
        var now = DateTime.UtcNow;

        // Periodic cleanup of stale entries
        CleanupStaleEntries(now);

        var queue = _requestLog.GetOrAdd(clientIp, _ => new Queue<DateTime>());

        lock (queue)
        {
            // Remove timestamps outside the window
            while (queue.Count > 0 && now - queue.Peek() > Window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= MaxRequests)
            {
                var logger = context.GetLogger<RateLimitingMiddleware>();
                logger.LogWarning("Rate limit exceeded for IP {ClientIp}: {Count} requests in window", clientIp, queue.Count);

                var response = httpRequestData.CreateResponse(HttpStatusCode.TooManyRequests);
                response.Headers.Add("Retry-After", "60");
                response.Headers.Add("Content-Type", "application/json");
                response.WriteString(JsonSerializer.Serialize(new
                {
                    error = "Rate limit exceeded",
                    message = "Too many requests. Please try again later.",
                    retryAfterSeconds = 60
                }));

                context.GetInvocationResult().Value = response;
                return;
            }

            queue.Enqueue(now);
        }

        await next(context);
    }

    private static string GetClientIp(HttpRequestData request)
    {
        if (request.Headers.TryGetValues("X-Forwarded-For", out var forwardedFor))
        {
            var ip = forwardedFor.FirstOrDefault();
            if (!string.IsNullOrEmpty(ip))
            {
                // X-Forwarded-For can contain "ip:port" or "ip, proxy1, proxy2"
                return ip.Split(',')[0].Trim().Split(':')[0];
            }
        }

        if (request.Headers.TryGetValues("X-Client-IP", out var clientIp))
        {
            var ip = clientIp.FirstOrDefault();
            if (!string.IsNullOrEmpty(ip))
            {
                return ip.Trim();
            }
        }

        return "unknown";
    }

    private static void CleanupStaleEntries(DateTime now)
    {
        if (now - _lastCleanup < CleanupInterval) return;

        lock (_cleanupLock)
        {
            if (now - _lastCleanup < CleanupInterval) return;
            _lastCleanup = now;

            var staleKeys = _requestLog
                .Where(kvp =>
                {
                    lock (kvp.Value)
                    {
                        return kvp.Value.Count == 0 || now - kvp.Value.Peek() > Window;
                    }
                })
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in staleKeys)
            {
                _requestLog.TryRemove(key, out _);
            }
        }
    }
}
