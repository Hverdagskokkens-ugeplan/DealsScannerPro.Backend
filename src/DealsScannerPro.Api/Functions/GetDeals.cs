using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using DealsScannerPro.Api.Services;

namespace DealsScannerPro.Api.Functions;

public class GetDeals
{
    private readonly ITableStorageService _storageService;
    private readonly ILogger<GetDeals> _logger;

    public GetDeals(ITableStorageService storageService, ILogger<GetDeals> logger)
    {
        _storageService = storageService;
        _logger = logger;
    }

    [Function("GetDeals")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "deals")] HttpRequestData req)
    {
        try
        {
            // Parse query parameters
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var butik = query["butik"];
            var kategori = query["kategori"];
            var limit = int.TryParse(query["limit"], out var l) ? l : 100;

            var tilbud = await _storageService.GetTilbudAsync(butik, kategori, limit);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                tilbud = tilbud.Select(t => new
                {
                    id = $"{t.PartitionKey}|{t.RowKey}",
                    t.Produkt,
                    total_pris = t.TotalPris,
                    pris_per_enhed = t.PrisPerEnhed,
                    t.Enhed,
                    t.Maengde,
                    t.Kategori,
                    t.Konfidens,
                    t.Butik,
                    gyldig_fra = t.GyldigFra.ToString("yyyy-MM-dd"),
                    gyldig_til = t.GyldigTil.ToString("yyyy-MM-dd"),
                    t.Side
                }),
                total = tilbud.Count
            });

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting deals");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = "Failed to get deals", details = ex.Message });
            return errorResponse;
        }
    }

    [Function("GetDealById")]
    public async Task<HttpResponseData> GetById(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "deals/{id}")] HttpRequestData req,
        string id)
    {
        try
        {
            // ID format: partitionKey|rowKey
            var parts = id.Split('|');
            if (parts.Length != 2)
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "Invalid ID format" });
                return badRequest;
            }

            var tilbud = await _storageService.GetTilbudByIdAsync(parts[0], parts[1]);

            if (tilbud == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { error = "Deal not found" });
                return notFound;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                id = $"{tilbud.PartitionKey}|{tilbud.RowKey}",
                tilbud.Produkt,
                total_pris = tilbud.TotalPris,
                pris_per_enhed = tilbud.PrisPerEnhed,
                tilbud.Enhed,
                tilbud.Maengde,
                tilbud.Kategori,
                tilbud.Konfidens,
                tilbud.Butik,
                gyldig_fra = tilbud.GyldigFra.ToString("yyyy-MM-dd"),
                gyldig_til = tilbud.GyldigTil.ToString("yyyy-MM-dd"),
                tilbud.Side,
                tilbud.KildeFil,
                tilbud.Varianter,
                tilbud.Kommentar
            });

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting deal by ID");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = "Failed to get deal", details = ex.Message });
            return errorResponse;
        }
    }
}
