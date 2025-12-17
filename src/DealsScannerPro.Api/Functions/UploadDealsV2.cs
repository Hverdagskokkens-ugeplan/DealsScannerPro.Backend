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
/// V2 Upload endpoint for the new scanner pipeline.
/// Handles offers with full normalization, confidence scoring, and SKU keys.
/// </summary>
public class UploadDealsV2
{
    private readonly IOfferService _offerService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UploadDealsV2> _logger;

    public UploadDealsV2(
        IOfferService offerService,
        IConfiguration configuration,
        ILogger<UploadDealsV2> logger)
    {
        _offerService = offerService;
        _configuration = configuration;
        _logger = logger;
    }

    [Function("UploadDealsV2")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "management/upload/v2")] HttpRequestData req)
    {
        // Validate API key
        var expectedApiKey = _configuration["AdminApiKey"];
        var providedApiKey = req.Headers.TryGetValues("x-api-key", out var keys)
            ? keys.FirstOrDefault()
            : null;

        if (string.IsNullOrEmpty(providedApiKey) || providedApiKey != expectedApiKey)
        {
            _logger.LogWarning("Unauthorized v2 upload attempt");
            var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauthorizedResponse.WriteAsJsonAsync(new { error = "Invalid or missing API key" });
            return unauthorizedResponse;
        }

        try
        {
            var request = await req.ReadFromJsonAsync<ScannerUploadRequest>();

            if (request == null || request.Offers.Count == 0)
            {
                _logger.LogWarning("Bad request: no offers provided");
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteAsJsonAsync(new { error = "No offers provided" });
                return badRequestResponse;
            }

            _logger.LogInformation(
                "V2 Upload: {Count} offers from {Retailer}, version={Version}",
                request.Offers.Count,
                request.Meta?.Retailer ?? "unknown",
                request.Version);

            // Log scan stats
            if (request.ScanStats != null)
            {
                _logger.LogInformation(
                    "Scan stats: pages={Pages}, blocks={Blocks}, detected={Detected}, extracted={Extracted}",
                    request.ScanStats.TotalPages,
                    request.ScanStats.TotalBlocks,
                    request.ScanStats.OffersDetected,
                    request.ScanStats.OffersExtracted);
            }

            // Count by status
            var autoPublished = request.Offers.Count(o => o.Status == "published");
            var needsReview = request.Offers.Count(o => o.Status == "needs_review");
            var lowConfidence = request.Offers.Count(o => o.Status == "low_confidence");

            _logger.LogInformation(
                "Offers by status: auto_published={Auto}, needs_review={Review}, low_confidence={Low}",
                autoPublished, needsReview, lowConfidence);

            // Log first few offers for debugging
            foreach (var offer in request.Offers.Take(3))
            {
                _logger.LogInformation(
                    "  [{Status}] {Product} - {Price} kr (conf={Conf:F2}, sku={Sku})",
                    offer.Status,
                    offer.ProductNorm?[..Math.Min(40, offer.ProductNorm?.Length ?? 0)],
                    offer.PriceValue,
                    offer.Confidence,
                    offer.SkuKey?[..Math.Min(30, offer.SkuKey?.Length ?? 0)]);
            }

            // Save offers
            var result = await _offerService.SaveOffersAsync(request);

            _logger.LogInformation(
                "V2 Upload complete: saved={Saved}, skipped={Skipped}, errors={Errors}",
                result.Saved, result.Skipped, result.Errors);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                version = "2.0",
                offers_received = request.Offers.Count,
                offers_saved = result.Saved,
                offers_skipped = result.Skipped,
                offers_errors = result.Errors,
                auto_published = autoPublished,
                needs_review = needsReview,
                retailer = request.Meta?.Retailer,
                valid_from = request.Meta?.ValidFrom,
                valid_to = request.Meta?.ValidTo
            });

            return response;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON parsing error in v2 upload");
            var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await errorResponse.WriteAsJsonAsync(new { error = "Invalid JSON format", details = ex.Message });
            return errorResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in v2 upload");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = "Failed to upload offers", details = ex.Message });
            return errorResponse;
        }
    }
}
