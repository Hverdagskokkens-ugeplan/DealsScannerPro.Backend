using System.Net;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using DealsScannerPro.Api.Services;

namespace DealsScannerPro.Api.Functions;

/// <summary>
/// API endpoint for reprocessing existing offers with retailer rules.
/// POST /api/management/reprocess
/// </summary>
public class ReprocessEndpoints
{
    private readonly IReprocessService _reprocessService;
    private readonly ILogger<ReprocessEndpoints> _logger;

    public ReprocessEndpoints(IReprocessService reprocessService, ILogger<ReprocessEndpoints> logger)
    {
        _reprocessService = reprocessService;
        _logger = logger;
    }

    /// <summary>
    /// Reprocess offers for a flyer by applying active retailer rules.
    /// POST /api/management/reprocess
    /// </summary>
    [Function("Reprocess")]
    public async Task<HttpResponseData> Reprocess(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "management/reprocess")] HttpRequestData req)
    {
        try
        {
            var request = await req.ReadFromJsonAsync<ReprocessRequest>();

            if (request == null || string.IsNullOrWhiteSpace(request.FlyerId))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "flyer_id is required" });
                return badRequest;
            }

            if (!request.FlyerId.Contains('_'))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "flyer_id must contain '_' separator (format: retailer_YYYY-MM-DD_YYYY-MM-DD)" });
                return badRequest;
            }

            var triggeredBy = request.TriggeredBy ?? "anonymous";

            var result = await _reprocessService.ReprocessAsync(request.FlyerId, triggeredBy, request.Reason);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(MapResultToResponse(result));
            return response;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No offers found"))
        {
            _logger.LogWarning(ex, "No offers found for reprocess request");
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = ex.Message });
            return notFound;
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid reprocess request");
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new { error = ex.Message });
            return badRequest;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during reprocess");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    private static object MapResultToResponse(ReprocessResult r)
    {
        return new
        {
            processing_run_id = r.ProcessingRunId,
            flyer_id = r.FlyerId,
            retailer = r.Retailer,
            total_offers = r.TotalOffers,
            rules_applied = r.RulesApplied,
            offers_modified = r.OffersModified,
            offers_skipped = r.OffersSkipped,
            errors = r.Errors,
            before = new
            {
                high_confidence = r.Before.HighConfidence,
                low_confidence = r.Before.LowConfidence,
                avg_confidence = r.Before.AvgConfidence,
                published = r.Before.Published,
                needs_review = r.Before.NeedsReview,
                deleted = r.Before.Deleted
            },
            after = new
            {
                high_confidence = r.After.HighConfidence,
                low_confidence = r.After.LowConfidence,
                avg_confidence = r.After.AvgConfidence,
                published = r.After.Published,
                needs_review = r.After.NeedsReview,
                deleted = r.After.Deleted
            },
            improvement = new
            {
                high_confidence_delta = r.Improvement.HighConfidenceDelta,
                avg_confidence_delta = r.Improvement.AvgConfidenceDelta
            },
            rules_applied_details = r.RulesAppliedDetails.Select(d => new
            {
                rule_id = d.RuleId,
                rule_type = d.RuleType,
                hit_count = d.HitCount
            })
        };
    }
}

// Request DTO
public class ReprocessRequest
{
    [JsonPropertyName("flyer_id")]
    public string FlyerId { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("triggered_by")]
    public string? TriggeredBy { get; set; }
}
