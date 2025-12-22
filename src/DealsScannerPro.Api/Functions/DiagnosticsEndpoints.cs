using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using DealsScannerPro.Api.Models;
using DealsScannerPro.Api.Services;

namespace DealsScannerPro.Api.Functions;

/// <summary>
/// Diagnostics API endpoints for scan logs and system health.
/// </summary>
public class DiagnosticsEndpoints
{
    private readonly IScanLogService _scanLogService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DiagnosticsEndpoints> _logger;

    public DiagnosticsEndpoints(
        IScanLogService scanLogService,
        IConfiguration configuration,
        ILogger<DiagnosticsEndpoints> logger)
    {
        _scanLogService = scanLogService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Get recent scan logs.
    /// GET /api/diagnostics/scans?limit=20
    /// </summary>
    [Function("GetScanLogs")]
    public async Task<HttpResponseData> GetScanLogs(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "diagnostics/scans")] HttpRequestData req)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var limit = int.TryParse(query["limit"], out var l) ? Math.Min(l, 100) : 20;

            _logger.LogInformation("Getting scan logs, limit={Limit}", limit);

            var scans = await _scanLogService.GetScansAsync(limit);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                count = scans.Count,
                scans
            });

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting scan logs");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Log a scan run (called by scanner after each scan).
    /// POST /api/diagnostics/scans
    /// </summary>
    [Function("LogScan")]
    public async Task<HttpResponseData> LogScan(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "diagnostics/scans")] HttpRequestData req)
    {
        try
        {
            // Validate API key
            var expectedApiKey = _configuration["AdminApiKey"];
            var providedApiKey = req.Headers.TryGetValues("x-api-key", out var keys)
                ? keys.FirstOrDefault()
                : null;

            if (string.IsNullOrEmpty(providedApiKey) || providedApiKey != expectedApiKey)
            {
                _logger.LogWarning("Unauthorized scan log attempt");
                var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorizedResponse.WriteAsJsonAsync(new { error = "Invalid or missing API key" });
                return unauthorizedResponse;
            }

            var request = await req.ReadFromJsonAsync<LogScanRequest>();

            if (request == null)
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "Invalid request body" });
                return badRequest;
            }

            _logger.LogInformation("Logging scan for {SourceFile}", request.SourceFile);

            var scanLog = await _scanLogService.LogScanAsync(request);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                scan_id = scanLog.ScanId
            });

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging scan");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }
}
