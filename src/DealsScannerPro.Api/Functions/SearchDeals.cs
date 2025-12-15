using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using DealsScannerPro.Api.Services;

namespace DealsScannerPro.Api.Functions;

public class SearchDeals
{
    private readonly ITableStorageService _storageService;
    private readonly IFuzzySearchService _fuzzyService;
    private readonly ILogger<SearchDeals> _logger;

    public SearchDeals(
        ITableStorageService storageService,
        IFuzzySearchService fuzzyService,
        ILogger<SearchDeals> logger)
    {
        _storageService = storageService;
        _fuzzyService = fuzzyService;
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
}
