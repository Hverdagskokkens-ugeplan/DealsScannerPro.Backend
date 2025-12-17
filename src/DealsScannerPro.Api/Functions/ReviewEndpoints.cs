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
/// Review UI API endpoints for the human review workflow.
/// </summary>
public class ReviewEndpoints
{
    private readonly IOfferService _offerService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReviewEndpoints> _logger;

    public ReviewEndpoints(
        IOfferService offerService,
        IConfiguration configuration,
        ILogger<ReviewEndpoints> logger)
    {
        _offerService = offerService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Get offers pending review, sorted by lowest confidence first.
    /// GET /api/review/queue?limit=50&retailer=netto
    /// </summary>
    [Function("ReviewQueue")]
    public async Task<HttpResponseData> GetQueue(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "review/queue")] HttpRequestData req)
    {
        try
        {
            // Parse query params
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var limit = int.TryParse(query["limit"], out var l) ? Math.Min(l, 100) : 50;
            var retailer = query["retailer"];

            _logger.LogInformation("Review queue request: limit={Limit}, retailer={Retailer}", limit, retailer);

            // Get offers needing review
            var offers = await _offerService.GetOffersForReviewAsync(limit);

            // Filter by retailer if specified
            if (!string.IsNullOrEmpty(retailer))
            {
                offers = offers.Where(o => o.Retailer.Equals(retailer, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            // Calculate stats
            var allPending = await _offerService.GetOffersAsync(new OfferQuery
            {
                Status = OfferStatus.NeedsReview,
                MaxResults = 1000
            });

            var stats = new
            {
                pending = allPending.Count,
                published = 0, // Would need separate query for today's published
                rejected = 0,
                avg_confidence = offers.Count > 0 ? offers.Average(o => o.Confidence) : 0
            };

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                offers = offers.Select(MapOfferToResponse),
                stats
            });

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting review queue");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Update an offer (approve, reject, edit).
    /// POST /api/review/update
    /// </summary>
    [Function("ReviewUpdate")]
    public async Task<HttpResponseData> UpdateOffer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "review/update")] HttpRequestData req)
    {
        try
        {
            var request = await req.ReadFromJsonAsync<ReviewUpdateRequest>();

            if (request == null || string.IsNullOrEmpty(request.PartitionKey) || string.IsNullOrEmpty(request.RowKey))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "Missing partition_key or row_key" });
                return badRequest;
            }

            _logger.LogInformation("Review update: {PK}/{RK} -> {Status} by {User}",
                request.PartitionKey, request.RowKey, request.Status, request.ReviewedBy);

            var update = new OfferUpdate
            {
                Status = request.Status,
                BrandNorm = request.BrandNorm,
                ProductNorm = request.ProductNorm,
                VariantNorm = request.VariantNorm,
                Category = request.Category,
                PriceValue = request.PriceValue,
                NetAmountValue = request.NetAmountValue,
                NetAmountUnit = request.NetAmountUnit,
                Comment = request.Comment,
                Reason = request.Reason
            };

            var offer = await _offerService.UpdateOfferAsync(
                request.PartitionKey,
                request.RowKey,
                update,
                request.ReviewedBy ?? "anonymous");

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                offer = MapOfferToResponse(offer)
            });

            return response;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Offer not found");
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = "Offer not found" });
            return notFound;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating offer");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Batch approve multiple offers.
    /// POST /api/review/batch-approve
    /// </summary>
    [Function("ReviewBatchApprove")]
    public async Task<HttpResponseData> BatchApprove(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "review/batch-approve")] HttpRequestData req)
    {
        try
        {
            var request = await req.ReadFromJsonAsync<BatchApproveRequest>();

            if (request?.OfferIds == null || request.OfferIds.Count == 0)
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "No offer_ids provided" });
                return badRequest;
            }

            _logger.LogInformation("Batch approve: {Count} offers by {User}",
                request.OfferIds.Count, request.ReviewedBy);

            await _offerService.BatchApproveAsync(request.OfferIds, request.ReviewedBy ?? "anonymous");

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                approved = request.OfferIds.Count
            });

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in batch approve");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Batch reject multiple offers.
    /// POST /api/review/batch-reject
    /// </summary>
    [Function("ReviewBatchReject")]
    public async Task<HttpResponseData> BatchReject(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "review/batch-reject")] HttpRequestData req)
    {
        try
        {
            var request = await req.ReadFromJsonAsync<BatchRejectRequest>();

            if (request?.OfferIds == null || request.OfferIds.Count == 0)
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "No offer_ids provided" });
                return badRequest;
            }

            _logger.LogInformation("Batch reject: {Count} offers by {User}",
                request.OfferIds.Count, request.ReviewedBy);

            foreach (var offerId in request.OfferIds)
            {
                var parts = offerId.Split('|');
                if (parts.Length == 2)
                {
                    try
                    {
                        await _offerService.DeleteOfferAsync(
                            parts[0],
                            parts[1],
                            request.ReviewedBy ?? "anonymous",
                            request.Reason ?? "Batch rejected");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to reject offer: {OfferId}", offerId);
                    }
                }
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                rejected = request.OfferIds.Count
            });

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in batch reject");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Simple magic link authentication (placeholder).
    /// In production, use proper email service.
    /// POST /api/auth/magic-link
    /// </summary>
    [Function("AuthMagicLink")]
    public async Task<HttpResponseData> SendMagicLink(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/magic-link")] HttpRequestData req)
    {
        try
        {
            var request = await req.ReadFromJsonAsync<MagicLinkRequest>();

            if (string.IsNullOrEmpty(request?.Email))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "Email required" });
                return badRequest;
            }

            // Validate email domain (whitelist)
            var allowedDomains = _configuration["AllowedEmailDomains"]?.Split(',')
                ?? new[] { "@example.com" };

            var isAllowed = allowedDomains.Any(d =>
                request.Email.EndsWith(d, StringComparison.OrdinalIgnoreCase));

            if (!isAllowed)
            {
                _logger.LogWarning("Magic link request from unauthorized domain: {Email}", request.Email);
                var unauthorized = req.CreateResponse(HttpStatusCode.Forbidden);
                await unauthorized.WriteAsJsonAsync(new { error = "Email domain not authorized" });
                return unauthorized;
            }

            // Generate simple token (in production, use proper token generation + email sending)
            var token = GenerateToken();

            _logger.LogInformation("Magic link generated for {Email}: {Token}", request.Email, token);

            // In production: send email with link
            // For now: return success (client shows token input)

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                message = "Magic link sent",
                // Only include token in development
                debug_token = _configuration["ASPNETCORE_ENVIRONMENT"] == "Development" ? token : null
            });

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending magic link");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Verify magic link token.
    /// POST /api/auth/verify
    /// </summary>
    [Function("AuthVerify")]
    public async Task<HttpResponseData> VerifyToken(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/verify")] HttpRequestData req)
    {
        try
        {
            var request = await req.ReadFromJsonAsync<VerifyTokenRequest>();

            if (string.IsNullOrEmpty(request?.Email) || string.IsNullOrEmpty(request?.Token))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "Email and token required" });
                return badRequest;
            }

            // Simple token validation (in production, validate against stored tokens)
            // For demo: accept "demo123" or any 6+ char token
            if (request.Token.Length >= 6 || request.Token == "demo123")
            {
                var sessionToken = GenerateToken();

                _logger.LogInformation("User verified: {Email}", request.Email);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    session_token = sessionToken,
                    email = request.Email,
                    expires_in = 86400 // 24 hours
                });

                return response;
            }

            var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauthorized.WriteAsJsonAsync(new { error = "Invalid token" });
            return unauthorized;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying token");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    private static string GenerateToken()
    {
        var bytes = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace("+", "").Replace("/", "")[..32];
    }

    private static object MapOfferToResponse(Offer offer)
    {
        return new
        {
            id = $"{offer.PartitionKey}|{offer.RowKey}",
            partition_key = offer.PartitionKey,
            row_key = offer.RowKey,
            retailer = offer.Retailer,
            valid_from = offer.ValidFrom.ToString("yyyy-MM-dd"),
            valid_to = offer.ValidTo.ToString("yyyy-MM-dd"),
            product_text_raw = offer.ProductTextRaw,
            brand_norm = offer.BrandNorm,
            product_norm = offer.ProductNorm,
            variant_norm = offer.VariantNorm,
            category = offer.Category,
            price_value = offer.PriceValue,
            deposit_value = offer.DepositValue,
            price_excl_deposit = offer.PriceExclDeposit,
            unit_price_value = offer.UnitPriceValue,
            unit_price_unit = offer.UnitPriceUnit,
            net_amount_value = offer.NetAmountValue,
            net_amount_unit = offer.NetAmountUnit,
            pack_count = offer.PackCount,
            container_type = offer.ContainerType,
            sku_key = offer.SkuKey,
            comment = offer.Comment,
            confidence = offer.Confidence,
            confidence_reasons = ParseJson<List<string>>(offer.ConfidenceReasonsJson),
            crop_url = offer.CropUrl,
            status = offer.Status,
            review_reason = offer.ReviewReason,
            created_at = offer.CreatedAt,
            reviewed_at = offer.ReviewedAt,
            reviewed_by = offer.ReviewedBy,
            trace = ParseJson<OfferTrace>(offer.TraceJson)
        };
    }

    private static T? ParseJson<T>(string? json) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return null;
        }
    }
}

// Request models
public class ReviewUpdateRequest
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public string? Status { get; set; }
    public string? BrandNorm { get; set; }
    public string? ProductNorm { get; set; }
    public string? VariantNorm { get; set; }
    public string? Category { get; set; }
    public double? PriceValue { get; set; }
    public double? NetAmountValue { get; set; }
    public string? NetAmountUnit { get; set; }
    public string? Comment { get; set; }
    public string? Reason { get; set; }
    public string? ReviewedBy { get; set; }
}

public class BatchApproveRequest
{
    public List<string> OfferIds { get; set; } = new();
    public string? ReviewedBy { get; set; }
}

public class BatchRejectRequest
{
    public List<string> OfferIds { get; set; } = new();
    public string? ReviewedBy { get; set; }
    public string? Reason { get; set; }
}

public class MagicLinkRequest
{
    public string Email { get; set; } = string.Empty;
}

public class VerifyTokenRequest
{
    public string Email { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
}
