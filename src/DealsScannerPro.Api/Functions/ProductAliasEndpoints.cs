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
/// CRUD API endpoints for product aliases (cross-store product matching).
/// </summary>
public class ProductAliasEndpoints
{
    private readonly IProductAliasService _aliasService;
    private readonly ILogger<ProductAliasEndpoints> _logger;

    public ProductAliasEndpoints(IProductAliasService aliasService, ILogger<ProductAliasEndpoints> logger)
    {
        _aliasService = aliasService;
        _logger = logger;
    }

    /// <summary>
    /// List all product aliases, optionally filtered by search query.
    /// GET /api/product-aliases?q=lurpak
    /// </summary>
    [Function("GetProductAliases")]
    public async Task<HttpResponseData> GetProductAliases(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "product-aliases")] HttpRequestData req)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var q = query["q"];

            List<ProductAlias> aliases;
            if (!string.IsNullOrEmpty(q))
            {
                aliases = await _aliasService.SearchAliasesAsync(q);
            }
            else
            {
                aliases = await _aliasService.GetAliasesAsync();
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                aliases = aliases.Select(MapAliasToResponse),
                count = aliases.Count
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting product aliases");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Get a single product alias with members.
    /// GET /api/product-aliases/{id}
    /// </summary>
    [Function("GetProductAlias")]
    public async Task<HttpResponseData> GetProductAlias(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "product-aliases/{id}")] HttpRequestData req,
        string id)
    {
        try
        {
            var alias = await _aliasService.GetAliasByIdAsync(id);

            if (alias == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { error = $"Product alias '{id}' not found" });
                return notFound;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(MapAliasToResponse(alias));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting product alias {Id}", id);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Create a new product alias with initial members.
    /// POST /api/product-aliases
    /// </summary>
    [Function("CreateProductAlias")]
    public async Task<HttpResponseData> CreateProductAlias(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "product-aliases")] HttpRequestData req)
    {
        try
        {
            var request = await req.ReadFromJsonAsync<CreateProductAliasRequest>();

            if (request == null
                || string.IsNullOrWhiteSpace(request.CanonicalBrand)
                || string.IsNullOrWhiteSpace(request.CanonicalProduct))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "canonical_brand and canonical_product are required" });
                return badRequest;
            }

            var createRequest = new CreateAliasRequest
            {
                CanonicalBrand = request.CanonicalBrand,
                CanonicalProduct = request.CanonicalProduct,
                CanonicalVariant = request.CanonicalVariant,
                CreatedBy = request.CreatedBy ?? "anonymous",
                InitialMembers = request.InitialMembers?.Select(m => new AddMemberRequest
                {
                    SkuKey = m.SkuKey,
                    Retailer = m.Retailer,
                    BrandNorm = m.BrandNorm,
                    ProductNorm = m.ProductNorm,
                    VariantNorm = m.VariantNorm,
                    AddedBy = m.AddedBy
                }).ToList() ?? new()
            };

            var alias = await _aliasService.CreateAliasAsync(createRequest);

            _logger.LogInformation("Product alias created: {AliasId}", alias.RowKey);

            var response = req.CreateResponse(HttpStatusCode.Created);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                alias = MapAliasToResponse(alias)
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating product alias");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Add a member to an existing product alias.
    /// PUT /api/product-aliases/{id}/members
    /// </summary>
    [Function("AddProductAliasMember")]
    public async Task<HttpResponseData> AddProductAliasMember(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "product-aliases/{id}/members")] HttpRequestData req,
        string id)
    {
        try
        {
            var request = await req.ReadFromJsonAsync<ProductAliasMemberRequest>();

            if (request == null
                || string.IsNullOrWhiteSpace(request.SkuKey)
                || string.IsNullOrWhiteSpace(request.Retailer))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "sku_key and retailer are required" });
                return badRequest;
            }

            var addRequest = new AddMemberRequest
            {
                SkuKey = request.SkuKey,
                Retailer = request.Retailer,
                BrandNorm = request.BrandNorm,
                ProductNorm = request.ProductNorm,
                VariantNorm = request.VariantNorm,
                AddedBy = request.AddedBy
            };

            var alias = await _aliasService.AddMemberAsync(id, addRequest);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                alias = MapAliasToResponse(alias)
            });
            return response;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to add member to alias {Id}", id);
            var statusCode = ex.Message.Contains("already a member")
                ? HttpStatusCode.Conflict
                : HttpStatusCode.NotFound;
            var errorResponse = req.CreateResponse(statusCode);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding member to alias {Id}", id);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Remove a member from a product alias.
    /// DELETE /api/product-aliases/{id}/members/{retailer}/{skuKeyHash}
    /// </summary>
    [Function("RemoveProductAliasMember")]
    public async Task<HttpResponseData> RemoveProductAliasMember(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "product-aliases/{id}/members/{retailer}/{skuKeyHash}")] HttpRequestData req,
        string id,
        string retailer,
        string skuKeyHash)
    {
        try
        {
            // We receive the hash in the URL, but the service needs the original skuKey.
            // Since we can't reverse the hash, look up the member first to get the skuKey.
            var alias = await _aliasService.GetAliasByIdAsync(id);
            if (alias == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { error = $"Alias '{id}' not found" });
                return notFound;
            }

            // Find the skuKey from MembersJson
            string? skuKey = null;
            if (!string.IsNullOrEmpty(alias.MembersJson))
            {
                var members = JsonSerializer.Deserialize<List<ProductAliasMemberDto>>(alias.MembersJson);
                var member = members?.FirstOrDefault(m =>
                    m.Retailer.Equals(retailer, StringComparison.OrdinalIgnoreCase) &&
                    ProductAliasService.HashSkuKey(m.SkuKey) == skuKeyHash);
                skuKey = member?.SkuKey;
            }

            if (skuKey == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { error = $"Member with hash '{skuKeyHash}' not found in alias '{id}'" });
                return notFound;
            }

            alias = await _aliasService.RemoveMemberAsync(id, skuKey, retailer);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                alias = MapAliasToResponse(alias)
            });
            return response;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Alias not found: {Id}", id);
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = ex.Message });
            return notFound;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing member from alias {Id}", id);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Deactivate (soft delete) a product alias.
    /// DELETE /api/product-aliases/{id}
    /// </summary>
    [Function("DeactivateProductAlias")]
    public async Task<HttpResponseData> DeactivateProductAlias(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "product-aliases/{id}")] HttpRequestData req,
        string id)
    {
        try
        {
            await _aliasService.DeactivateAliasAsync(id);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, message = $"Product alias '{id}' deactivated" });
            return response;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Product alias not found: {Id}", id);
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = ex.Message });
            return notFound;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deactivating product alias {Id}", id);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Suggest matching aliases for given product attributes.
    /// GET /api/product-alias-suggest?brand_norm=Lurpak&product_norm=Smor
    /// </summary>
    [Function("SuggestProductAliases")]
    public async Task<HttpResponseData> SuggestProductAliases(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "product-alias-suggest")] HttpRequestData req)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var brandNorm = query["brand_norm"];
            var productNorm = query["product_norm"];
            var variantNorm = query["variant_norm"];

            if (string.IsNullOrWhiteSpace(productNorm))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "product_norm is required" });
                return badRequest;
            }

            var suggestions = await _aliasService.SuggestMatchesAsync(brandNorm, productNorm, variantNorm);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                suggestions = suggestions.Select(s => new
                {
                    alias_id = s.AliasId,
                    canonical_brand = s.CanonicalBrand,
                    canonical_product = s.CanonicalProduct,
                    canonical_variant = s.CanonicalVariant,
                    member_count = s.MemberCount,
                    score = s.Score,
                    match_reason = s.MatchReason
                }),
                count = suggestions.Count
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error suggesting product aliases");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Resolve a SKU key to its product alias.
    /// GET /api/product-alias-resolve?sku_key=...&retailer=netto
    /// </summary>
    [Function("ResolveProductAlias")]
    public async Task<HttpResponseData> ResolveProductAlias(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "product-alias-resolve")] HttpRequestData req)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var skuKey = query["sku_key"];
            var retailer = query["retailer"];

            if (string.IsNullOrWhiteSpace(skuKey) || string.IsNullOrWhiteSpace(retailer))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "sku_key and retailer are required" });
                return badRequest;
            }

            var alias = await _aliasService.ResolveAliasAsync(skuKey, retailer);

            if (alias == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { error = "No alias found for this SKU key" });
                return notFound;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(MapAliasToResponse(alias));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resolving product alias");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    private static object MapAliasToResponse(ProductAlias alias)
    {
        List<ProductAliasMemberDto>? members = null;
        if (!string.IsNullOrEmpty(alias.MembersJson))
        {
            try
            {
                members = JsonSerializer.Deserialize<List<ProductAliasMemberDto>>(alias.MembersJson);
            }
            catch (JsonException)
            {
                // Return null if deserialization fails
            }
        }

        return new
        {
            id = alias.RowKey,
            canonical_brand = alias.CanonicalBrand,
            canonical_product = alias.CanonicalProduct,
            canonical_variant = alias.CanonicalVariant,
            keywords = alias.Keywords,
            members,
            member_count = alias.MemberCount,
            is_active = alias.IsActive,
            created_by = alias.CreatedBy,
            created_at = alias.CreatedAt,
            updated_at = alias.UpdatedAt,
            match_count = alias.MatchCount
        };
    }
}

// --- Request DTOs ---

public class CreateProductAliasRequest
{
    [JsonPropertyName("canonical_brand")]
    public string CanonicalBrand { get; set; } = string.Empty;

    [JsonPropertyName("canonical_product")]
    public string CanonicalProduct { get; set; } = string.Empty;

    [JsonPropertyName("canonical_variant")]
    public string? CanonicalVariant { get; set; }

    [JsonPropertyName("initial_members")]
    public List<ProductAliasMemberRequest>? InitialMembers { get; set; }

    [JsonPropertyName("created_by")]
    public string? CreatedBy { get; set; }
}

public class ProductAliasMemberRequest
{
    [JsonPropertyName("sku_key")]
    public string SkuKey { get; set; } = string.Empty;

    [JsonPropertyName("retailer")]
    public string Retailer { get; set; } = string.Empty;

    [JsonPropertyName("brand_norm")]
    public string? BrandNorm { get; set; }

    [JsonPropertyName("product_norm")]
    public string? ProductNorm { get; set; }

    [JsonPropertyName("variant_norm")]
    public string? VariantNorm { get; set; }

    [JsonPropertyName("added_by")]
    public string? AddedBy { get; set; }
}
