using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using DealsScannerPro.Api.Models;
using DealsScannerPro.Api.Services;

namespace DealsScannerPro.Api.Functions;

/// <summary>
/// CRUD API endpoints for SKU overrides (MATCH/SPLIT).
/// Stored in Azure Table Storage (table: SkuOverrides).
/// </summary>
public class SkuOverrideEndpoints
{
    private readonly ISkuOverrideService _skuOverrideService;
    private readonly ILogger<SkuOverrideEndpoints> _logger;

    public SkuOverrideEndpoints(ISkuOverrideService skuOverrideService, ILogger<SkuOverrideEndpoints> logger)
    {
        _skuOverrideService = skuOverrideService;
        _logger = logger;
    }

    /// <summary>
    /// List active SKU overrides for a retailer, optionally filtered by type.
    /// GET /api/sku-overrides/{retailer}?type=MATCH
    /// </summary>
    [Function("GetSkuOverrides")]
    public async Task<HttpResponseData> GetSkuOverrides(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sku-overrides/{retailer}")] HttpRequestData req,
        string retailer)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var overrideType = query["type"];

            var overrides = await _skuOverrideService.GetOverridesAsync(retailer, overrideType);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                overrides = overrides.Select(MapOverrideToResponse),
                count = overrides.Count,
                retailer = retailer.ToLowerInvariant(),
                filter_type = overrideType
            });

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting SKU overrides for {Retailer}", retailer);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Get a single SKU override by ID.
    /// GET /api/sku-overrides/{retailer}/{id}
    /// </summary>
    [Function("GetSkuOverride")]
    public async Task<HttpResponseData> GetSkuOverride(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sku-overrides/{retailer}/{id}")] HttpRequestData req,
        string retailer,
        string id)
    {
        try
        {
            var skuOverride = await _skuOverrideService.GetOverrideAsync(retailer, id);

            if (skuOverride == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { error = $"Override '{id}' not found for retailer '{retailer}'" });
                return notFound;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(MapOverrideToResponse(skuOverride));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting SKU override {Retailer}/{Id}", retailer, id);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Create a new SKU override (MATCH or SPLIT).
    /// POST /api/sku-overrides
    /// </summary>
    [Function("CreateSkuOverride")]
    public async Task<HttpResponseData> CreateSkuOverride(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sku-overrides")] HttpRequestData req)
    {
        try
        {
            var request = await req.ReadFromJsonAsync<CreateSkuOverrideRequest>();

            if (request == null
                || string.IsNullOrWhiteSpace(request.Retailer)
                || string.IsNullOrWhiteSpace(request.OverrideType)
                || string.IsNullOrWhiteSpace(request.SkuA))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "retailer, override_type, and sku_a are required" });
                return badRequest;
            }

            // Serialize split_into to JSON for storage
            string? splitIntoJson = null;
            if (request.SplitInto != null && request.SplitInto.Count > 0)
            {
                splitIntoJson = JsonSerializer.Serialize(request.SplitInto);
            }

            var skuOverride = new SkuOverride
            {
                Retailer = request.Retailer,
                OverrideType = request.OverrideType,
                SkuA = request.SkuA,
                SkuB = request.SkuB,
                SplitIntoJson = splitIntoJson,
                Reason = request.Reason,
                CreatedBy = request.CreatedBy ?? "anonymous"
            };

            var created = await _skuOverrideService.CreateOverrideAsync(skuOverride);

            _logger.LogInformation("SKU override created: {RowKey} for {Retailer}", created.RowKey, created.PartitionKey);

            var response = req.CreateResponse(HttpStatusCode.Created);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                sku_override = MapOverrideToResponse(created)
            });

            return response;
        }
        catch (ArgumentException ex)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new { error = ex.Message });
            return badRequest;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating SKU override");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Deactivate (soft delete) a SKU override.
    /// DELETE /api/sku-overrides/{retailer}/{id}
    /// </summary>
    [Function("DeactivateSkuOverride")]
    public async Task<HttpResponseData> DeactivateSkuOverride(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sku-overrides/{retailer}/{id}")] HttpRequestData req,
        string retailer,
        string id)
    {
        try
        {
            await _skuOverrideService.DeactivateOverrideAsync(retailer, id);

            _logger.LogInformation("SKU override deactivated: {Retailer}/{Id}", retailer, id);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, message = $"SKU override '{id}' deactivated" });
            return response;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "SKU override not found: {Retailer}/{Id}", retailer, id);
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = ex.Message });
            return notFound;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deactivating SKU override {Retailer}/{Id}", retailer, id);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    private static object MapOverrideToResponse(SkuOverride o)
    {
        List<SplitTarget>? splitInto = null;
        if (!string.IsNullOrEmpty(o.SplitIntoJson))
        {
            try
            {
                splitInto = JsonSerializer.Deserialize<List<SplitTarget>>(o.SplitIntoJson);
            }
            catch (JsonException)
            {
                // Return raw JSON as-is if deserialization fails
            }
        }

        return new
        {
            id = o.RowKey,
            retailer = o.PartitionKey,
            override_type = o.OverrideType,
            sku_a = o.SkuA,
            sku_b = o.SkuB,
            split_into = splitInto,
            reason = o.Reason,
            created_by = o.CreatedBy,
            created_at = o.CreatedAt,
            is_active = o.IsActive
        };
    }
}

// Request DTO
public class CreateSkuOverrideRequest
{
    [JsonPropertyName("retailer")]
    public string Retailer { get; set; } = string.Empty;

    [JsonPropertyName("override_type")]
    public string OverrideType { get; set; } = string.Empty;

    [JsonPropertyName("sku_a")]
    public string SkuA { get; set; } = string.Empty;

    [JsonPropertyName("sku_b")]
    public string? SkuB { get; set; }

    [JsonPropertyName("split_into")]
    public List<SplitTarget>? SplitInto { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("created_by")]
    public string? CreatedBy { get; set; }
}
