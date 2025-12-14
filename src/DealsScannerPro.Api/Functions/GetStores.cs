using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using DealsScannerPro.Api.Services;

namespace DealsScannerPro.Api.Functions;

public class GetStores
{
    private readonly ITableStorageService _storageService;
    private readonly ILogger<GetStores> _logger;

    public GetStores(ITableStorageService storageService, ILogger<GetStores> logger)
    {
        _storageService = storageService;
        _logger = logger;
    }

    [Function("GetStores")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "stores")] HttpRequestData req)
    {
        try
        {
            var butikker = await _storageService.GetButikkerAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                stores = butikker.Select(b => new
                {
                    id = b.RowKey,
                    b.Navn,
                    logo_url = b.LogoUrl,
                    primaer_farve = b.PrimaerFarve,
                    sekundaer_farve = b.SekundaerFarve,
                    b.Aktiv
                })
            });

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting stores");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = "Failed to get stores", details = ex.Message });
            return errorResponse;
        }
    }

    [Function("SeedStores")]
    public async Task<HttpResponseData> Seed(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "admin/seed-stores")] HttpRequestData req)
    {
        try
        {
            await _storageService.SeedButikkerAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, message = "Stores seeded successfully" });

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error seeding stores");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = "Failed to seed stores", details = ex.Message });
            return errorResponse;
        }
    }
}
