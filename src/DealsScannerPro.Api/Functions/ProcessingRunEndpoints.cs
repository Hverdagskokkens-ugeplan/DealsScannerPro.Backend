using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using DealsScannerPro.Api.Services;

namespace DealsScannerPro.Api.Functions;

public class ProcessingRunEndpoints
{
    private readonly IProcessingRunService _runService;
    private readonly ILogger<ProcessingRunEndpoints> _logger;

    public ProcessingRunEndpoints(IProcessingRunService runService, ILogger<ProcessingRunEndpoints> logger)
    {
        _runService = runService;
        _logger = logger;
    }

    /// <summary>
    /// Get all processing runs for a flyer.
    /// GET /api/runs/{flyerId}
    /// </summary>
    [Function("GetProcessingRuns")]
    public async Task<HttpResponseData> GetRuns(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "runs/{flyerId}")] HttpRequestData req,
        string flyerId)
    {
        try
        {
            var runs = await _runService.GetRunsAsync(flyerId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                flyer_id = flyerId,
                runs = runs.Select(MapRunToResponse),
                count = runs.Count
            });

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting processing runs for {FlyerId}", flyerId);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Get the latest processing run for a flyer.
    /// GET /api/runs/{flyerId}/latest
    /// </summary>
    [Function("GetLatestProcessingRun")]
    public async Task<HttpResponseData> GetLatestRun(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "runs/{flyerId}/latest")] HttpRequestData req,
        string flyerId)
    {
        try
        {
            var run = await _runService.GetLatestRunAsync(flyerId);

            if (run == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { error = $"No processing runs found for flyer '{flyerId}'" });
                return notFound;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(MapRunToResponse(run));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting latest processing run for {FlyerId}", flyerId);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Compare two processing runs for a flyer.
    /// GET /api/runs/{flyerId}/compare?run1=X&amp;run2=Y
    /// </summary>
    [Function("CompareProcessingRuns")]
    public async Task<HttpResponseData> CompareRuns(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "runs/{flyerId}/compare")] HttpRequestData req,
        string flyerId)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var runId1 = query["run1"];
            var runId2 = query["run2"];

            if (string.IsNullOrEmpty(runId1) || string.IsNullOrEmpty(runId2))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "Both 'run1' and 'run2' query parameters are required" });
                return badRequest;
            }

            var comparison = await _runService.CompareRunsAsync(flyerId, runId1, runId2);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                flyer_id = flyerId,
                run1 = MapSummaryToResponse(comparison.Run1),
                run2 = MapSummaryToResponse(comparison.Run2),
                delta = new
                {
                    total_offers = comparison.Delta.TotalOffersDelta,
                    high_confidence = comparison.Delta.HighConfidenceDelta,
                    medium_confidence = comparison.Delta.MediumConfidenceDelta,
                    low_confidence = comparison.Delta.LowConfidenceDelta,
                    avg_confidence = comparison.Delta.AvgConfidenceDelta
                }
            });

            return response;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Compare runs failed for {FlyerId}", flyerId);
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = ex.Message });
            return notFound;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error comparing processing runs for {FlyerId}", flyerId);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    private static object MapRunToResponse(Models.ProcessingRun run)
    {
        var metrics = run.GetMetrics();
        return new
        {
            run_id = run.RunId,
            flyer_id = run.FlyerId,
            retailer = run.Retailer,
            run_number = run.RunNumber,
            started_at = run.StartedAt,
            completed_at = run.CompletedAt,
            status = run.Status,
            trigger_type = run.TriggerType,
            triggered_by = run.TriggeredBy,
            rules_version_hash = run.RulesVersionHash,
            offers_created = run.OffersCreated,
            offers_updated = run.OffersUpdated,
            metrics = metrics != null ? new
            {
                total_offers = metrics.TotalOffers,
                high_confidence = metrics.HighConfidence,
                medium_confidence = metrics.MediumConfidence,
                low_confidence = metrics.LowConfidence,
                avg_confidence = metrics.AvgConfidence,
                by_category = metrics.ByCategory
            } : null,
            errors = run.GetErrors()
        };
    }

    private static object MapSummaryToResponse(RunSummary summary)
    {
        return new
        {
            run_id = summary.RunId,
            run_number = summary.RunNumber,
            trigger_type = summary.TriggerType,
            started_at = summary.StartedAt,
            completed_at = summary.CompletedAt,
            status = summary.Status,
            metrics = summary.Metrics != null ? new
            {
                total_offers = summary.Metrics.TotalOffers,
                high_confidence = summary.Metrics.HighConfidence,
                medium_confidence = summary.Metrics.MediumConfidence,
                low_confidence = summary.Metrics.LowConfidence,
                avg_confidence = summary.Metrics.AvgConfidence,
                by_category = summary.Metrics.ByCategory
            } : null
        };
    }
}
