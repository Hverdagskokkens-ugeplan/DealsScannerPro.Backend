using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using DealsScannerPro.Api.Services;

namespace DealsScannerPro.Api.Functions;

public class ShoppingListEndpoints
{
    private readonly IOfferService _offerService;
    private readonly ILogger<ShoppingListEndpoints> _logger;

    public ShoppingListEndpoints(IOfferService offerService, ILogger<ShoppingListEndpoints> logger)
    {
        _offerService = offerService;
        _logger = logger;
    }

    [Function("MatchShoppingList")]
    public async Task<HttpResponseData> MatchShoppingList(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "match-shopping-list")] HttpRequestData req)
    {
        try
        {
            var body = await req.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "Request body is required" });
                return badRequest;
            }

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var request = JsonSerializer.Deserialize<MatchShoppingListRequest>(body, options);

            if (request?.Items == null || request.Items.Count == 0)
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "items is required and must not be empty" });
                return badRequest;
            }

            if (request.Items.Count > 50)
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "Maximum 50 items allowed" });
                return badRequest;
            }

            // Map to service request
            var serviceRequest = new ShoppingListRequest
            {
                Items = request.Items,
                Date = request.Date,
                Retailers = request.Retailers
            };

            var result = await _offerService.MatchShoppingListAsync(serviceRequest);

            // Map to snake_case response
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                results = result.Results.Select(r => new
                {
                    search_term = r.SearchTerm,
                    matches = r.Matches.Select(m => new
                    {
                        offer_id = m.OfferId,
                        retailer = m.Retailer,
                        product_norm = m.ProductNorm,
                        brand_norm = m.BrandNorm,
                        variant_norm = m.VariantNorm,
                        product_text_raw = m.ProductTextRaw,
                        category = m.Category,
                        price_value = m.PriceValue,
                        unit_price_value = m.UnitPriceValue,
                        unit_price_unit = m.UnitPriceUnit,
                        net_amount_value = m.NetAmountValue,
                        net_amount_unit = m.NetAmountUnit,
                        valid_from = m.ValidFrom.ToString("yyyy-MM-dd"),
                        valid_to = m.ValidTo.ToString("yyyy-MM-dd"),
                        match_score = m.MatchScore
                    })
                }),
                summary = new
                {
                    total_items = result.Summary.TotalItems,
                    items_matched = result.Summary.ItemsMatched,
                    items_not_found = result.Summary.ItemsNotFound,
                    not_found = result.Summary.NotFound
                }
            });

            return response;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON in shopping list request");
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new { error = "Invalid JSON", details = ex.Message });
            return badRequest;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error matching shopping list");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = "Failed to match shopping list", details = ex.Message });
            return errorResponse;
        }
    }
}

public class MatchShoppingListRequest
{
    [JsonPropertyName("items")]
    public List<string> Items { get; set; } = new();

    [JsonPropertyName("date")]
    public DateTime? Date { get; set; }

    [JsonPropertyName("retailers")]
    public List<string>? Retailers { get; set; }
}
