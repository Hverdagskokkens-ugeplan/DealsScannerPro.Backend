using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using DealsScannerPro.Api.Models;
using DealsScannerPro.Api.Services;

namespace DealsScannerPro.Api.Functions;

public class SearchDeals
{
    private readonly ITableStorageService _storageService;
    private readonly IFuzzySearchService _fuzzyService;
    private readonly IProductAliasService _aliasService;
    private readonly ILogger<SearchDeals> _logger;

    public SearchDeals(
        ITableStorageService storageService,
        IFuzzySearchService fuzzyService,
        IProductAliasService aliasService,
        ILogger<SearchDeals> logger)
    {
        _storageService = storageService;
        _fuzzyService = fuzzyService;
        _aliasService = aliasService;
        _logger = logger;
    }

    [Function("SearchDeals")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "tilbud/search")] HttpRequestData req)
    {
        try
        {
            // Parse query parameters
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var q = query["q"];
            var datoParam = query["dato"];
            var butikkerParam = query["butikker"];
            var groupBy = query["group_by"];

            // Parse date (default to today)
            var dato = DateTime.Today;
            if (!string.IsNullOrEmpty(datoParam) && DateTime.TryParse(datoParam, out var parsedDate))
            {
                dato = parsedDate;
            }

            // Parse butikker (comma-separated)
            var butikker = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(butikkerParam))
            {
                foreach (var b in butikkerParam.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    butikker.Add(b.Trim().ToLowerInvariant());
                }
            }

            _logger.LogInformation("Search: q={Query}, dato={Date}, butikker={Stores}",
                q ?? "(alle)", dato.ToString("yyyy-MM-dd"), butikker.Count > 0 ? string.Join(",", butikker) : "(alle)");

            // Get all tilbud valid for the date
            var tilbud = await _storageService.GetTilbudByDateAsync(dato);

            // Filter by stores if specified
            if (butikker.Count > 0)
            {
                tilbud = tilbud.Where(t => butikker.Contains(t.Butik?.ToLowerInvariant() ?? "")).ToList();
            }

            // Apply fuzzy search if query specified
            if (!string.IsNullOrEmpty(q))
            {
                tilbud = tilbud.Where(t =>
                    _fuzzyService.IsFuzzyMatch(t.Produkt ?? "", q) ||
                    _fuzzyService.IsFuzzyMatch(t.Kategori ?? "", q)
                ).ToList();
            }

            // group_by=product: cross-store product grouping
            if (string.Equals(groupBy, "product", StringComparison.OrdinalIgnoreCase))
            {
                return await BuildGroupedResponse(req, tilbud);
            }

            // Sort by price (low to high) and map to response
            var results = tilbud
                .OrderBy(t => t.TotalPris ?? double.MaxValue)
                .Select(t => new
                {
                    id = $"{t.PartitionKey}|{t.RowKey}",
                    t.Produkt,
                    total_pris = t.TotalPris,
                    pris_per_enhed = t.PrisPerEnhed,
                    t.Enhed,
                    t.Maengde,
                    maengde_normaliseret = t.MaengdeValue.HasValue ? new
                    {
                        value = t.MaengdeValue,
                        unit = t.MaengdeUnit
                    } : null,
                    t.Kategori,
                    t.Konfidens,
                    t.Butik,
                    gyldig_fra = t.GyldigFra.ToString("yyyy-MM-dd"),
                    gyldig_til = t.GyldigTil.ToString("yyyy-MM-dd"),
                    t.Side,
                    t.KildeFil,
                    Varianter = string.IsNullOrEmpty(t.Varianter) ? null : JsonSerializer.Deserialize<List<string>>(t.Varianter),
                    t.Kommentar,
                    oprettet = t.Oprettet.ToString("yyyy-MM-ddTHH:mm:ssZ")
                })
                .ToList();

            _logger.LogInformation("Search returned {Count} results", results.Count);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(results);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching deals");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = "Failed to search deals", details = ex.Message });
            return errorResponse;
        }
    }

    private async Task<HttpResponseData> BuildGroupedResponse(HttpRequestData req, List<Tilbud> tilbud)
    {
        // Collect unique retailers and batch resolve alias mappings
        var retailers = tilbud
            .Select(t => t.Butik?.ToLowerInvariant() ?? "")
            .Where(b => !string.IsNullOrEmpty(b))
            .Distinct()
            .ToList();

        // SKU key → alias ID mapping per offer
        var offerAliasMap = new Dictionary<string, string>(); // "{partitionKey}|{rowKey}" → aliasId
        foreach (var retailer in retailers)
        {
            var retailerOffers = tilbud.Where(t =>
                string.Equals(t.Butik, retailer, StringComparison.OrdinalIgnoreCase)).ToList();

            var skuKeys = retailerOffers
                .Select(t => t.RowKey)
                .Where(k => !string.IsNullOrEmpty(k))
                .Distinct()
                .ToList();

            if (skuKeys.Count == 0) continue;

            var resolved = await _aliasService.BatchResolveAsync(retailer, skuKeys);
            foreach (var offer in retailerOffers)
            {
                if (resolved.TryGetValue(offer.RowKey, out var aliasId))
                {
                    offerAliasMap[$"{offer.PartitionKey}|{offer.RowKey}"] = aliasId;
                }
            }
        }

        // Get alias details for resolved IDs
        var aliasIds = offerAliasMap.Values.Distinct().ToList();
        var aliasDetails = new Dictionary<string, ProductAlias>();
        foreach (var aliasId in aliasIds)
        {
            var alias = await _aliasService.GetAliasByIdAsync(aliasId);
            if (alias != null)
            {
                aliasDetails[aliasId] = alias;
            }
        }

        // Group offers: by alias_id if available, otherwise by product name
        var groups = new List<object>();
        var grouped = tilbud.GroupBy(t =>
        {
            var key = $"{t.PartitionKey}|{t.RowKey}";
            return offerAliasMap.TryGetValue(key, out var aliasId)
                ? $"alias:{aliasId}"
                : $"product:{t.Produkt?.ToLowerInvariant() ?? ""}";
        });

        foreach (var group in grouped)
        {
            var offers = group
                .OrderBy(t => t.TotalPris ?? double.MaxValue)
                .ToList();

            var bestOffer = offers.First();
            string? aliasId = null;
            string? canonicalProduct = null;
            string? canonicalBrand = null;

            if (group.Key.StartsWith("alias:"))
            {
                aliasId = group.Key["alias:".Length..];
                if (aliasDetails.TryGetValue(aliasId, out var alias))
                {
                    canonicalProduct = alias.CanonicalProduct;
                    canonicalBrand = alias.CanonicalBrand;
                }
            }

            groups.Add(new
            {
                canonical_product = canonicalProduct ?? bestOffer.Produkt,
                canonical_brand = canonicalBrand,
                alias_id = aliasId,
                offers = offers.Select(t => new
                {
                    id = $"{t.PartitionKey}|{t.RowKey}",
                    retailer = t.Butik,
                    t.Produkt,
                    total_pris = t.TotalPris,
                    pris_per_enhed = t.PrisPerEnhed,
                    t.Enhed,
                    t.Maengde,
                    t.Kategori,
                    gyldig_fra = t.GyldigFra.ToString("yyyy-MM-dd"),
                    gyldig_til = t.GyldigTil.ToString("yyyy-MM-dd")
                }),
                best_price = bestOffer.TotalPris,
                best_retailer = bestOffer.Butik,
                offer_count = offers.Count
            });
        }

        // Sort groups by best price
        var sortedGroups = groups
            .OrderBy(g => ((dynamic)g).best_price ?? double.MaxValue)
            .ToList();

        _logger.LogInformation("Search returned {Groups} groups with {Offers} total offers",
            sortedGroups.Count, tilbud.Count);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            groups = sortedGroups,
            total_groups = sortedGroups.Count,
            total_offers = tilbud.Count
        });
        return response;
    }
}
