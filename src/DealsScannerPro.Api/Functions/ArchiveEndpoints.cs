using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using DealsScannerPro.Api.Services;

namespace DealsScannerPro.Api.Functions;

public class ArchiveEndpoints
{
    private readonly IArchiveService _archiveService;
    private readonly ILogger<ArchiveEndpoints> _logger;

    public ArchiveEndpoints(IArchiveService archiveService, ILogger<ArchiveEndpoints> logger)
    {
        _archiveService = archiveService;
        _logger = logger;
    }

    /// <summary>
    /// Manually trigger an archive run.
    /// POST /api/management/trigger-archive
    /// </summary>
    [Function("TriggerArchive")]
    public async Task<HttpResponseData> TriggerArchive(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "management/trigger-archive")] HttpRequestData req)
    {
        try
        {
            _logger.LogInformation("Manual archive triggered");
            var result = await _archiveService.RunArchiveAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                result.Enabled,
                result.GraceDays,
                result.RetentionDays,
                result.OffersArchived,
                result.TilbudArchived,
                result.OffersHardDeleted,
                result.TilbudHardDeleted,
                result.RunAt,
                error_count = result.Errors.Count,
                errors = result.Errors
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running archive");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Get archived offers.
    /// GET /api/archive/offers?retailer=netto&product=smor&from=2025-01-01&to=2025-06-01&max_results=100
    /// </summary>
    [Function("GetArchivedOffers")]
    public async Task<HttpResponseData> GetArchivedOffers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "archive/offers")] HttpRequestData req)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var retailer = query["retailer"];
            var product = query["product"];
            var from = DateTime.TryParse(query["from"], out var f) ? f : (DateTime?)null;
            var to = DateTime.TryParse(query["to"], out var t) ? t : (DateTime?)null;
            var maxResults = int.TryParse(query["max_results"], out var m) ? m : 100;

            var offers = await _archiveService.GetArchivedOffersAsync(retailer, product, from, to, maxResults);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                count = offers.Count,
                offers = offers.Select(o => new
                {
                    partition_key = o.PartitionKey,
                    row_key = o.RowKey,
                    retailer = o.Retailer,
                    product = o.ProductNorm,
                    brand = o.BrandNorm,
                    product_text_raw = o.ProductTextRaw,
                    category = o.Category,
                    price = o.PriceValue,
                    unit_price = o.UnitPriceValue,
                    unit_price_unit = o.UnitPriceUnit,
                    valid_from = o.ValidFrom,
                    valid_to = o.ValidTo,
                    status = o.Status
                })
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting archived offers");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }
}
